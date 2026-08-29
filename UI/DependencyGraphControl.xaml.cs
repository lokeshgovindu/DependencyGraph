using DependencyGraph.Scan;
using EnvDTE80;
using Microsoft.Msagl.Drawing;
using Microsoft.Msagl.WpfGraphControl;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MsaglNode = Microsoft.Msagl.Drawing.Node;
using SaveFileDialog = System.Windows.Forms.SaveFileDialog;

namespace DependencyGraph.UI
{
    /// <summary>
    /// The dependency graph tool window's contents: project picker, the MSAGL graph surface and
    /// a status bar. Layout is MSAGL's job - see <see cref="GraphBuilder"/>.
    /// </summary>
    public partial class DependencyGraphControl : UserControl
    {
        private const string ShowDeepestReferencesOnly = "Deepest references only";
        private const string ShowAllReferences         = "All references";
        private const string ShowProjectReferencesOnly = "Project references only";
        private const string ShowReferencedBy          = "Referenced by (impact)";
        private const string ShowEntireSolution        = "Entire solution";

        private readonly Dictionary<string, ConnectionsVisibility> _displayMethods =
            new Dictionary<string, ConnectionsVisibility>
            {
                { ShowDeepestReferencesOnly, ConnectionsVisibility.Deepest },
                { ShowAllReferences,         ConnectionsVisibility.All     },
                { ShowProjectReferencesOnly, ConnectionsVisibility.Project },
                { ShowReferencedBy,          ConnectionsVisibility.ReferencedBy },
                { ShowEntireSolution,        ConnectionsVisibility.Solution },
            };

        private readonly Dictionary<string, LayerDirection> _directions =
            new Dictionary<string, LayerDirection>
            {
                { "Top to bottom", LayerDirection.TB },
                { "Left to right", LayerDirection.LR },
                { "Bottom to top", LayerDirection.BT },
                { "Right to left", LayerDirection.RL },
            };

        private readonly GraphViewer _viewer = new GraphViewer();
        private GraphPalette _palette;

        private DTE2 _dte;
        private VSScanner _vsScanner;
        private ProjectReferenceTree _prt;
        private Graph _graph;

        /// <summary>Set while the controls are being populated, so handlers do not fire early.</summary>
        private bool _loading;

        private MsaglNode _selected;

        /// <summary>Whether the current graph has already been framed to the window.</summary>
        private bool _hasBeenFitted;

        /// <summary>The folder-name elements drawn on the current graph.</summary>
        private GraphBuilder.FolderNames _folderNames = new GraphBuilder.FolderNames();

        private System.Windows.Media.Brush _statLabelBrush;
        private System.Windows.Media.Brush _statValueBrush;

        public DependencyGraphControl()
        {
            InitializeComponent();

            _palette = GraphPalette.ForVsTheme();
            ApplyTheme();

            // Repaint if the user switches VS theme while the window is open. Without this the
            // colours stay as they were when the window was created, which can leave dark text
            // on a light background or the reverse.
            //
            // Tied to Loaded/Unloaded rather than subscribed once in the constructor: a tool
            // window can be unloaded and shown again, and unsubscribing on unload without
            // re-subscribing left theme tracking dead for the rest of the session. ThemeChanged
            // is also a static event, so staying subscribed after unload would keep this control
            // alive.
            Loaded   += (s, e) => { SubscribeToThemeChanges(true);  SubscribeToSolutionEvents(true); };
            Unloaded += (s, e) => { SubscribeToThemeChanges(false); SubscribeToSolutionEvents(false); };

            // MSAGL's own layout editor (LayoutEditingEnabled) is what would normally provide
            // dragging, and it must stay off: enabling it made the first drag take the whole IDE
            // down - process gone, no exception dialog, nothing in the activity log or in Windows
            // Error Reporting. Reproduced twice, with and without grouping. Dragging is done here
            // instead, in Viewer_MouseMove, which is a few lines and cannot do that.
            _viewer.LayoutEditingEnabled = false;
            _viewer.RunLayoutAsync = false;
            _viewer.BindToPanel(GraphHost);
            _viewer.MouseDown += Viewer_MouseDown;
            _viewer.MouseMove += Viewer_MouseMove;
            _viewer.MouseUp   += Viewer_MouseUp;
            _viewer.ViewChangeEvent += (s, e) => UpdateZoomIndicator();
            GraphHost.SizeChanged += GraphHost_SizeChanged;

            BuildNodeContextMenu();
            SetUpNodeTooltip();
            PopulateFixedControls();

            Btn_ZoomFit.Click           += (s, e) => { _viewer.SetInitialTransform(); UpdateZoomIndicator(); };
            Btn_Refresh.Click           += (s, e) => Rescan();
            Btn_ResetLayout.Click       += (s, e) => ResetPositions();
            Btn_Focus.Checked           += (s, e) => ApplyFocus();
            Btn_Focus.Unchecked         += (s, e) => { _focusKeys.Clear(); Rebuild(); };
            Btn_Group.Checked           += (s, e) => Rebuild();
            Btn_Group.Unchecked         += (s, e) => { _collapsedFolders.Clear(); Rebuild(); };
            Btn_Legend.Checked          += (s, e) => LegendPanel.Visibility = Visibility.Visible;
            Btn_Legend.Unchecked        += (s, e) => LegendPanel.Visibility = Visibility.Collapsed;
            Btn_SaveAs.Click            += Btn_SaveAs_Click;
            Btn_SaveSolutionDGML.Click  += Btn_SaveSolutionDGML_Click;

            CBProjectNames.SelectionChanged  += (s, e) => Rebuild();
            CBDisplayMethod.SelectionChanged += (s, e) => Rebuild();
            CBOrientation.SelectionChanged   += (s, e) => Rebuild();
            CBDepth.SelectionChanged         += (s, e) => Rebuild();

            PreviewKeyUp += (s, e) =>
            {
                if (e.Key == Key.S && (Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
                    SaveGraphImage(null);
            };
        }

        #region Setup

        /// <summary>
        /// Colours the command bar, graph host and status bar to suit the active theme.
        /// </summary>
        /// <remarks>
        /// Colours come from the graph palette rather than a shell colour key. Two other routes
        /// were tried and do not work here: a DynamicResource lookup of the shell's brush keys
        /// silently finds nothing, because those keys are not in this element's resource tree;
        /// and VSColorTheme.GetThemedColor returns a colour whose alpha is zero, so a brush built
        /// from it paints the text fully transparent. The palette already knows whether the theme
        /// is light or dark, and its colours are opaque by construction.
        /// </remarks>
        private void ApplyTheme()
        {
            var text = _palette.NodeText;
            var brush = Brush(text);

            var backBrush = Brush(_palette.Background);
            var lineBrush = Brush(_palette.NodeStroke);

            _statLabelBrush = Brush(_palette.StatLabel);
            _statValueBrush = Brush(_palette.StatValue);

            CommandBar.Background    = backBrush;
            CommandBar.BorderBrush   = lineBrush;
            BarSeparator.Background  = lineBrush;
            GraphHost.Background     = backBrush;
            StatusBarMain.Background  = backBrush;
            StatusBarMain.BorderBrush = lineBrush;
            LegendPanel.Background    = backBrush;
            LegendPanel.BorderBrush   = lineBrush;
            Btn_Legend.Foreground     = brush;
            Btn_Legend.Background     = backBrush;
            Btn_Legend.BorderBrush    = lineBrush;
            BuildLegend();

            // Foreground is an inherited TextElement property, so setting it on the two
            // containers reaches every TextBlock inside them.
            System.Windows.Documents.TextElement.SetForeground(CommandBar, brush);
            System.Windows.Documents.TextElement.SetForeground(StatusBarMain, brush);
            EmptyMessage.Foreground = brush;

            // Buttons and combo boxes each bring their own template with its own brushes, so
            // inheritance does not reach them.
            foreach (var button in new System.Windows.Controls.Primitives.ButtonBase[]
                     { Btn_ZoomFit, Btn_Refresh, Btn_ResetLayout, Btn_SaveAs, Btn_SaveSolutionDGML,
                       Btn_Group, Btn_Focus })
            {
                button.Foreground = brush;
                button.Background = backBrush;
                button.BorderBrush = lineBrush;
            }

            // The combo boxes are deliberately left with WPF's own chrome - light field, dark
            // text - which is readable under either VS theme. Recolouring them is not worth it:
            // the selected item is drawn by a TextBlock that ComboBox's ControlTemplate creates,
            // and implicit styles do not reach elements generated inside a template, so setting
            // a dark background left the text dark-on-dark and invisible.
        }

        /// <summary>
        /// Fills in the legend. Rebuilt whenever the theme changes, since it is showing the
        /// palette's own colours.
        /// </summary>
        private void BuildLegend()
        {
            LegendItems.Children.Clear();

            // The olive nodes are the project the graph is rooted at - except in the
            // whole-solution view, which has no root and marks the projects nothing references.
            AddLegendRow(_palette.RootFill, _palette.RootStroke,
                SelectedVisibility == ConnectionsVisibility.Solution
                    ? "Nothing in the solution references it"
                    : "The project being examined");
            AddLegendRow(_palette.NodeFill, _palette.NodeStroke, "Referenced project");
            AddLegendRow(_palette.ReferencingFill, _palette.NodeStroke, "References the selection");
            AddLegendRow(_palette.ReferencedFill, _palette.NodeStroke, "Referenced by the selection");
            AddLegendRow(_palette.NodeFill, _palette.SelectedStroke, "Selected");
            AddLegendRow(_palette.Background, _palette.NodeStroke,
                "A collapsed solution folder  (click it to expand)");
            AddLegendLine(_palette.Edge, "References  (arrow points at the dependency)");
            AddLegendLine(_palette.CycleEdge, "Circular reference");
        }

        private void AddLegendRow(Microsoft.Msagl.Drawing.Color fill,
            Microsoft.Msagl.Drawing.Color stroke, string text)
        {
            var swatch = new Border
            {
                Width = 22, Height = 13,
                Background = Brush(fill),
                BorderBrush = Brush(stroke),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            AddLegendItem(swatch, text);
        }

        private void AddLegendLine(Microsoft.Msagl.Drawing.Color colour, string text)
        {
            var line = new Border
            {
                Width = 22, Height = 2,
                Background = Brush(colour),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            AddLegendItem(line, text);
        }

        private void AddLegendItem(UIElement swatch, string text)
        {
            var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(swatch);
            row.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = Brush(_palette.NodeText),
                VerticalAlignment = VerticalAlignment.Center,
            });
            LegendItems.Children.Add(row);
        }

        /// <summary>Whether this control is currently listening for solution changes.</summary>
        private bool _listeningForSolutionEvents;

        /// <summary>
        /// Keeps the window in step with the IDE as solutions and projects come and go.
        /// </summary>
        /// <remarks>
        /// A tool window outlives the solution it scanned. Without this it went on showing the
        /// old graph after the solution closed - built on EnvDTE.Project objects that no longer
        /// refer to anything, so clicking a node could throw - and a newly opened solution was
        /// not picked up until Refresh was pressed.
        /// </remarks>
        private void SubscribeToSolutionEvents(bool subscribe)
        {
            if (subscribe == _listeningForSolutionEvents) return;

            var events = Community.VisualStudio.Toolkit.VS.Events.SolutionEvents;
            if (subscribe)
            {
                events.OnAfterOpenSolution  += OnSolutionOpened;
                events.OnAfterCloseSolution += OnSolutionClosed;
                events.OnAfterOpenProject   += OnProjectSetChanged;
                events.OnAfterRenameProject += OnProjectSetChanged;
                events.OnBeforeCloseProject += OnProjectSetChanged;
            }
            else
            {
                events.OnAfterOpenSolution  -= OnSolutionOpened;
                events.OnAfterCloseSolution -= OnSolutionClosed;
                events.OnAfterOpenProject   -= OnProjectSetChanged;
                events.OnAfterRenameProject -= OnProjectSetChanged;
                events.OnBeforeCloseProject -= OnProjectSetChanged;
            }

            _listeningForSolutionEvents = subscribe;
        }

        private void OnSolutionOpened(Community.VisualStudio.Toolkit.Solution solution)
        {
            // The picker and the graph both belong to the previous solution.
            ForgetSolution();
            Rescan();
        }

        private void OnSolutionClosed()
        {
            ForgetSolution();
            ShowEmptyMessage("Open a solution to see its project dependency graph.");
        }

        /// <summary>
        /// A project was added, removed or renamed. Rescanning keeps the picker honest.
        /// </summary>
        /// <remarks>
        /// Deferred to the dispatcher because these fire while the shell is still applying the
        /// change, and scanning mid-flight can see a half-updated solution.
        /// </remarks>
        private void OnProjectSetChanged(Community.VisualStudio.Toolkit.Project project)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_dte == null) return;
                ForgetSolution();
                Rescan();
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        /// <summary>Drops everything tied to the solution that was open.</summary>
        private void ForgetSolution()
        {
            _vsScanner = null;
            _prt = null;
            _graph = null;
            _selected = null;
            _selectedNodes.Clear();
            _focusKeys.Clear();
            _collapsedFolders.Clear();

            // The offsets stay on disk; dropping the path is what makes the next scan read them
            // back, including when the same solution is opened again.
            _dragOffsets.Clear();
            _solutionPath = null;
            _visuals = null;
            _edgeVisuals = null;
            _dragging = null;
            _hasBeenFitted = false;

            _loading = true;
            CBProjectNames.Items.Clear();
            _loading = false;

            // Dropping the graph clears the canvas; without it the old drawing stays on screen.
            try { _viewer.Graph = null; } catch { }
        }

        /// <summary>Whether this control is currently listening for theme changes.</summary>
        private bool _listeningForThemeChanges;

        private void SubscribeToThemeChanges(bool subscribe)
        {
            if (subscribe == _listeningForThemeChanges) return;

            if (subscribe) Microsoft.VisualStudio.PlatformUI.VSColorTheme.ThemeChanged += OnVsThemeChanged;
            else Microsoft.VisualStudio.PlatformUI.VSColorTheme.ThemeChanged -= OnVsThemeChanged;

            _listeningForThemeChanges = subscribe;
        }

        /// <summary>
        /// Re-picks the palette and rebuilds after a VS theme change.
        /// </summary>
        /// <remarks>
        /// The node and edge colours are baked into the MSAGL graph when it is built, so a full
        /// rebuild is the only way to recolour them consistently. Theme switching is rare enough
        /// that losing the current pan and zoom is a fair trade for not being left unreadable.
        /// </remarks>
        private void OnVsThemeChanged(Microsoft.VisualStudio.PlatformUI.ThemeChangedEventArgs e)
        {
            _palette = GraphPalette.ForVsTheme();
            ApplyTheme();
            Rebuild();
        }

        private static System.Windows.Media.SolidColorBrush Brush(Microsoft.Msagl.Drawing.Color c)
        {
            return new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(c.R, c.G, c.B));
        }

        private void PopulateFixedControls()
        {
            _loading = true;

            foreach (var name in _displayMethods.Keys) CBDisplayMethod.Items.Add(name);
            foreach (var name in _directions.Keys) CBOrientation.Items.Add(name);

            // Start from the saved defaults rather than always the first entry.
            CBDisplayMethod.SelectedItem = DefaultsFor(_settings.View) ?? ShowDeepestReferencesOnly;
            CBOrientation.SelectedItem   = DefaultsFor(_settings.Direction) ?? "Top to bottom";

            // "All" plus a few concrete cuts. Boxed ints, so SelectedDepth can read them back
            // without parsing.
            CBDepth.Items.Add(DepthAll);
            for (int depth = 1; depth <= 6; ++depth) CBDepth.Items.Add(depth);
            CBDepth.SelectedItem = _settings.Depth >= 1 && _settings.Depth <= 6
                ? (object)_settings.Depth
                : DepthAll;

            _loading = false;
        }

        /// <summary>
        /// Scans the current solution and shows the startup (or active) project. Safe to call
        /// again - the tool window stays open across solution changes, unlike the old dialog.
        /// </summary>
        /// <remarks>
        /// The first graph is deferred until the control is in the visual tree: MSAGL derives its
        /// initial zoom from the host panel's size, which is still zero before then, so building
        /// the graph too early leaves it scaled wrongly until the next resize.
        /// </remarks>
        public void Initialize(DTE2 dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _dte = dte;

            if (IsLoaded) { Rescan(); return; }
            Loaded += OnFirstLoad;
        }

        private void OnFirstLoad(object sender, RoutedEventArgs e)
        {
            Loaded -= OnFirstLoad;
            ThreadHelper.ThrowIfNotOnUIThread();
            Rescan();
        }

        /// <summary>
        /// Frames the graph the first time the host panel gets a real size, so it is positioned
        /// correctly however late WPF gets round to measuring the tool window.
        /// </summary>
        private void GraphHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_graph == null || _hasBeenFitted) return;
            if (GraphHost.ActualWidth <= 0 || GraphHost.ActualHeight <= 0) return;

            _hasBeenFitted = true;
            ShowAtActualSize();
        }

        /// <summary>
        /// Frames the graph at 100% zoom - centred on the graph when it fits, on the project
        /// being examined when it does not.
        /// </summary>
        /// <remarks>
        /// Deliberately not fit-to-window for the initial view: fitting scales the graph up to
        /// fill the space, which renders the node text oversized and soft. At 1:1 the labels are
        /// crisp. The Fit button is still there when the whole graph needs to be seen at once.
        /// </remarks>
        private void ShowAtActualSize()
        {
            // Deferred to after the layout pass: MSAGL rescales its transform by the change in
            // fit factor whenever the canvas is resized, so setting 100% before the tool window
            // has settled at its final size gets multiplied straight back out of 100%.
            Dispatcher.BeginInvoke(new Action(ApplyActualSize),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void ApplyActualSize()
        {
            if (_graph == null) return;

            if (_settings.FitOnOpen) { _viewer.SetInitialTransform(); UpdateZoomIndicator(); return; }

            var anchor = _graph.Nodes.FirstOrDefault(n => GraphBuilder.ProjectOf(n)?.DepthLevel == 0)
                      ?? _graph.Nodes.FirstOrDefault();

            if (anchor?.GeometryNode == null) { _viewer.SetInitialTransform(); UpdateZoomIndicator(); return; }

            _viewer.NodeToCenterWithScale(anchor, 1.0);

            // Centring on the root pushes the rest of the tree off to one side, which on a graph
            // that would have fitted anyway just wastes the window. So when the whole graph fits
            // at 1:1, centre the graph instead. A graph too big to fit keeps the root centred,
            // because then landing on the project you asked about is the useful thing.
            var box = _graph.BoundingBox;
            var viewport = _viewer.ClientViewportMappedToGraph;
            if (box.Width <= viewport.Width && box.Height <= viewport.Height)
                _viewer.PointToCenter(box.Center);

            UpdateZoomIndicator();
        }

        private void Rescan()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_dte == null)
            {
                ShowEmptyMessage("Visual Studio automation is not available yet. Try again in a moment.");
                return;
            }

            try
            {
                if (_dte.Solution == null || !_dte.Solution.IsOpen)
                {
                    ShowEmptyMessage("Open a solution to see its project dependency graph.");
                    return;
                }

                LoadStoredPositions(_dte.Solution.FullName);
                _vsScanner = new VSScanner(_dte);
            }
            catch (Exception ex)
            {
                _vsScanner = null;
                ShowEmptyMessage($"Could not scan the solution:\n\n{ex.Message}");
                return;
            }

            if (_vsScanner.Projects.Count == 0)
            {
                ShowEmptyMessage("This solution has no projects whose references can be read.");
                return;
            }

            _loading = true;
            string previousKey = SelectedProject?.Key;

            CBProjectNames.Items.Clear();
            var choices = _vsScanner.Projects.Select(p => new ProjectChoice(p)).ToList();

            // Only spell out the path for names that are ambiguous, so the list stays readable.
            foreach (var group in choices.GroupBy(c => c.Name))
                if (group.Count() > 1)
                    foreach (var choice in group) choice.ShowKey = true;

            foreach (var choice in choices.OrderBy(c => c.Name)) CBProjectNames.Items.Add(choice);
            _loading = false;

            HideEmptyMessage();
            SelectInitialProject(previousKey);
        }

        /// <summary>The picked project, or null while the picker is still being populated.</summary>
        private ProjectChoice SelectedProject => CBProjectNames.SelectedItem as ProjectChoice;

        private IEnumerable<ProjectChoice> Choices => CBProjectNames.Items.OfType<ProjectChoice>();

        private bool TrySelectByKey(string key)
        {
            if (key == null) return false;

            var match = Choices.FirstOrDefault(c => c.Key == key);
            if (match == null) return false;

            CBProjectNames.SelectedItem = match;
            return true;
        }

        private void SelectInitialProject(string previousKey)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (TrySelectByKey(previousKey)) return;

            var activeProjects = _dte.ActiveSolutionProjects as Array;
            if (activeProjects != null && activeProjects.Length > 0)
            {
                var active = activeProjects.GetValue(0) as EnvDTE.Project;
                if (active != null && TrySelectByKey(ProjectReferenceTree.KeyOf(active))) return;
            }

            if (_vsScanner.StartupProject != null
                && TrySelectByKey(ProjectReferenceTree.KeyOf(_vsScanner.StartupProject))) return;

            CBProjectNames.SelectedIndex = 0;
        }

        private void ShowEmptyMessage(string message)
        {
            EmptyMessage.Text = message;
            EmptyMessage.Visibility = Visibility.Visible;
            GraphHost.Visibility = Visibility.Collapsed;
            foreach (var field in new[] { SB_ProjectName, SB_Nodes, SB_Connections,
                                         SB_Zoom, SB_Selection, SB_ProjectsCount })
                field.Inlines.Clear();
        }

        private void HideEmptyMessage()
        {
            EmptyMessage.Visibility = Visibility.Collapsed;
            GraphHost.Visibility = Visibility.Visible;
        }

        #endregion

        #region Building the graph

        /// <summary>
        /// The view the picker is on, or the first one before the pickers have been filled in.
        /// </summary>
        /// <remarks>
        /// Reached from the constructor, by way of ApplyTheme and the legend, which runs before
        /// PopulateFixedControls has put anything in the combo boxes. Indexing the map with the
        /// null selection threw there, and an exception in a tool window's constructor shows the
        /// user a stack trace where the graph should be.
        /// </remarks>
        private ConnectionsVisibility SelectedVisibility => Selected(_displayMethods, CBDisplayMethod);

        private LayerDirection SelectedDirection => Selected(_directions, CBOrientation);

        private static T Selected<T>(Dictionary<string, T> map, ComboBox combo)
        {
            var name = combo?.SelectedItem as string;
            return name != null && map.TryGetValue(name, out var value) ? value : map.Values.First();
        }

        /// <summary>The "no limit" entry in the depth picker.</summary>
        private const string DepthAll = "All";

        /// <summary>
        /// Settings snapshot taken when the window is built, so the options page can be read
        /// without a shell dependency in the designer or a test harness.
        /// </summary>
        private readonly State.Settings _settings = LoadSettings();

        private static State.Settings LoadSettings()
        {
            try { return State.Settings.Instance; }
            catch { return new State.Settings(); }
        }

        /// <summary>Maps a saved enum default onto the matching combo entry.</summary>
        private string DefaultsFor(State.DefaultView view)
        {
            switch (view)
            {
                case State.DefaultView.AllReferences:         return ShowAllReferences;
                case State.DefaultView.ProjectReferencesOnly: return ShowProjectReferencesOnly;
                case State.DefaultView.ReferencedBy:          return ShowReferencedBy;
                default:                                      return ShowDeepestReferencesOnly;
            }
        }

        private string DefaultsFor(State.DefaultDirection direction)
        {
            switch (direction)
            {
                case State.DefaultDirection.LeftToRight: return "Left to right";
                case State.DefaultDirection.BottomToTop: return "Bottom to top";
                case State.DefaultDirection.RightToLeft: return "Right to left";
                default:                                 return "Top to bottom";
            }
        }

        /// <summary>Depth cut currently chosen, or <see cref="GraphBuilder.UnlimitedDepth"/>.</summary>
        private int SelectedDepth
            => CBDepth.SelectedItem is int depth ? depth : GraphBuilder.UnlimitedDepth;

        private void Rebuild()
        {
            if (_loading || _vsScanner == null) return;

            var choice = SelectedProject;
            if (choice == null) return;
            string projectName = choice.Name;

            ThreadHelper.ThrowIfNotOnUIThread();

            var visibility = SelectedVisibility;
            try
            {
                // Resolved by key, so a name shared by two projects picks the right one.
                switch (visibility)
                {
                    case ConnectionsVisibility.Project:
                        _prt = ProjectReferenceTree.GetProjectReferencesOnly(choice.Project);
                        break;

                    case ConnectionsVisibility.ReferencedBy:
                        // Needs the whole solution: a project does not know who references it.
                        _prt = ProjectReferenceTree.GetReferencedByTree(
                            choice.Project, _vsScanner.Projects);
                        break;

                    case ConnectionsVisibility.Solution:
                        // Every project, not just what one project can reach.
                        _prt = ProjectReferenceTree.GetSolutionTree(_vsScanner.Projects);
                        projectName = ShowEntireSolution;
                        break;

                    default:
                        _prt = new ProjectReferenceTree(choice.Project);
                        break;
                }
            }
            catch (Exception ex)
            {
                // Reading references goes through COM automation on projects that may be
                // unloading underneath us. Report it in the window rather than letting it
                // escape into the shell.
                _prt = null;
                ShowEmptyMessage($"Could not read the references of '{projectName}':\n\n{ex.Message}");
                return;
            }

            if (_prt == null)
            {
                ShowEmptyMessage($"Could not read the references of '{projectName}'.");
                return;
            }

            HideEmptyMessage();

            // The picker chooses the project a view is rooted at, and the whole-solution view is
            // not rooted at one - greying it out says so without hiding which project it would
            // go back to.
            bool rooted = visibility != ConnectionsVisibility.Solution;
            CBProjectNames.IsEnabled = rooted;
            Lbl_Project.Opacity = rooted ? 1.0 : 0.5;

            // Which projects are the solution's own, so references that lead outside it can be
            // drawn as such rather than looking like ordinary nodes with nothing beneath them.
            _prt.MarkExternals(Choices.Select(c => c.Key));

            _selected = null;
            _selectedNodes.Clear();
            _visuals = null;
            _edgeVisuals = null;
            _dragging = null;
            _graph = GraphBuilder.Build(_prt, visibility, SelectedDirection, _palette, SelectedDepth,
                folderOf: Btn_Group.IsChecked == true ? (Func<string, string>)_vsScanner.SolutionFolderOf : null,
                focusKeys: _focusKeys.Count > 0 ? _focusKeys : null,
                collapsedFolders: _collapsedFolders.Count > 0 ? _collapsedFolders : null);

            // Each rebuild makes new subgraph objects, so drop the previous registrations rather
            // than letting them accumulate in the viewer for the life of the window.
            _folderNames.Unregister(_viewer);
            _folderNames = GraphBuilder.RegisterSubgraphLabels(_graph, _viewer);

            _viewer.Graph = _graph;
            _folderNames.Reposition();
            ApplyStoredPositions();

            // The legend explains the root colour, which means something different here.
            BuildLegend();
            WatchForFolderCollapse();

            // Frame the new graph now if the panel is measured, otherwise leave it to
            // GraphHost_SizeChanged.
            _hasBeenFitted = GraphHost.ActualWidth > 0 && GraphHost.ActualHeight > 0;
            if (_hasBeenFitted) ShowAtActualSize();

            UpdateStatusBar(projectName);
        }

        private void UpdateStatusBar(string projectName)
        {
            SetStat(SB_ProjectName,   "Project",              projectName);
            SetStat(SB_Nodes,         "Nodes",                _graph.NodeCount.ToString());
            SetStat(SB_Connections,   "Connections",          _graph.EdgeCount.ToString());
            SetStat(SB_ProjectsCount, "Projects in solution", _vsScanner.Projects.Count.ToString());
            SB_Selection.Inlines.Clear();
            UpdateZoomIndicator();
        }

        /// <summary>
        /// Writes one "Name: value" status field, with the name bold and muted and the value in
        /// the accent colour, so the values are what the eye lands on.
        /// </summary>
        private void SetStat(TextBlock target, string name, string value)
        {
            target.Inlines.Clear();
            target.Inlines.Add(new System.Windows.Documents.Run(name + ": ")
            {
                FontWeight = FontWeights.SemiBold,
                Foreground = _statLabelBrush,
            });
            target.Inlines.Add(new System.Windows.Documents.Run(value)
            {
                Foreground = _statValueBrush,
            });
        }

        /// <summary>
        /// Shows the current zoom as a percentage, where 100% is the graph at its natural size.
        /// </summary>
        private void UpdateZoomIndicator()
        {
            if (_graph == null) { SB_Zoom.Inlines.Clear(); return; }

            try
            {
                // CurrentScale reads the canvas's render transform, which is not there before
                // the first graph has been laid out.
                SetStat(SB_Zoom, "Zoom", $"{_viewer.CurrentScale * 100:0}%");
            }
            catch
            {
                SB_Zoom.Inlines.Clear();
            }
        }

        #endregion

        #region Selection, highlighting and the node context menu

        private void Viewer_MouseDown(object sender, Microsoft.Msagl.Drawing.MsaglMouseEventArgs e)
        {
            var node = NodeUnderCursor();

            // Anything under the pointer can be dragged. Nothing moves until the pointer does,
            // so this costs an ordinary click nothing.
            if (e.LeftButtonIsPressed && _viewer.ObjectUnderMouseCursor is VNode vnode)
            {
                _dragging = vnode;
                _dragMoved = false;
                _lastDragPoint = ((Microsoft.Msagl.Drawing.IViewer)_viewer).ScreenToSource(e);
            }

            string collapsedFolder = GraphBuilder.CollapsedFolderOf(node);
            if (collapsedFolder != null)
            {
                // A collapsed folder has no chevron of its own to click, so the node itself is
                // the way back: click it to put the folder's projects on screen again.
                ExpandFolder(collapsedFolder);
                return;
            }

            if (e.Clicks > 1 && node != null)
            {
                // Double click drills into that project's own reference tree.
                ShowProject(node);
                return;
            }

            // Ctrl+click builds up a set, so several projects can be focused at once.
            bool additive = (Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
            Select(node, additive);
        }

        private MsaglNode NodeUnderCursor()
        {
            return (_viewer.ObjectUnderMouseCursor as VNode)?.Node;
        }

        #region Node tooltips

        /// <summary>
        /// Sets up the hover tooltip for the graph surface.
        /// </summary>
        /// <remarks>
        /// One tooltip on the host panel whose content is swapped as the pointer moves between
        /// nodes, rather than a tooltip per node: MSAGL owns the elements it draws and rebuilds
        /// them on every layout, so anything attached to them has to be reattached constantly.
        /// The graph itself can only show a short name, so this is where the rest of what is known
        /// about a project belongs - its path, its counts, its folder.
        /// </remarks>
        private void SetUpNodeTooltip()
        {
            // Has to start non-null, or WPF never raises ToolTipOpening for us to cancel.
            GraphHost.ToolTip = " ";

            // ToolTipOpening bubbles, so this fires for the node shapes too - which matters,
            // because MSAGL puts a tooltip of its own on each node's outline carrying just the
            // label. That one is on the innermost element under the pointer, so it wins; the way
            // past it is to replace the text on whichever element the event came from.
            GraphHost.ToolTipOpening += (s, e) =>
            {
                string text = TooltipFor(NodeUnderCursor());
                if (text == null) { e.Handled = true; return; }

                Retarget(e.OriginalSource as FrameworkElement, text);
            };
        }

        /// <summary>
        /// Puts the tooltip text on the element the pointer is actually over, falling back to the
        /// graph host.
        /// </summary>
        private void Retarget(FrameworkElement element, string text)
        {
            if (element != null && !ReferenceEquals(element, GraphHost)
                && element.ToolTip is string)
            {
                element.ToolTip = text;
                return;
            }

            GraphHost.ToolTip = text;
        }

        /// <summary>The node the tooltip text was last built for.</summary>
        private MsaglNode _hovered;

        /// <summary>
        /// Keeps the tooltip text in step with the pointer.
        /// </summary>
        /// <remarks>
        /// Belt and braces with the ToolTipOpening handler above, which would be enough on its own
        /// were MSAGL's idea of what is under the cursor not one mouse-move behind: it raises its
        /// move event first and hit-tests afterwards, so a tooltip built at the moment of opening
        /// can describe the node the pointer has just left.
        /// </remarks>
        private void UpdateHoverTooltip()
        {
            if (_dragging != null) return;

            var node = NodeUnderCursor();
            if (node == _hovered) return;

            _hovered = node;

            string text = TooltipFor(node);

            // Set it on the shape under the pointer as well as on the host, so the very first
            // hover shows this text rather than the label MSAGL put there.
            Retarget(Mouse.DirectlyOver as FrameworkElement, text ?? " ");
            GraphHost.ToolTip = text ?? " ";
        }

        /// <summary>What to say about the node under the pointer, or null for empty canvas.</summary>
        private string TooltipFor(MsaglNode node)
        {
            return NodeTooltip.For(node,
                _vsScanner == null ? (Func<string, string>)null : _vsScanner.SolutionFolderOf);
        }

        #endregion

        #region Dragging

        /// <summary>The node or folder box being dragged, null when not dragging.</summary>
        private VNode _dragging;

        private Microsoft.Msagl.Core.Geometry.Point _lastDragPoint;

        /// <summary>Whether the current drag has actually moved anything.</summary>
        private bool _dragMoved;

        /// <summary>
        /// Moves the node or folder box under the pointer, with its references following it.
        /// </summary>
        /// <remarks>
        /// Written out by hand rather than handed to MSAGL's LayoutEditor, which crashes the IDE
        /// outright - see the note where LayoutEditingEnabled is set. The viewer checks
        /// <c>Handled</c> before it pans, so marking the event handled is what stops a drag on a
        /// node from panning the whole graph instead.
        /// </remarks>
        private void Viewer_MouseMove(object sender, Microsoft.Msagl.Drawing.MsaglMouseEventArgs e)
        {
            if (_dragging == null) { UpdateHoverTooltip(); return; }

            if (!e.LeftButtonIsPressed) { EndDrag(); return; }

            var point = ((Microsoft.Msagl.Drawing.IViewer)_viewer).ScreenToSource(e);
            var delta = point - _lastDragPoint;
            _lastDragPoint = point;

            if (delta.Length < 0.001) return;

            MoveBy(_dragging, delta);
            _dragMoved = true;
            e.Handled = true;
        }

        private void Viewer_MouseUp(object sender, Microsoft.Msagl.Drawing.MsaglMouseEventArgs e)
        {
            EndDrag();
        }

        private void EndDrag()
        {
            if (_dragging == null) return;
            _dragging = null;

            // A folder name is placed against its box, so it has to follow the box.
            if (_dragMoved)
            {
                _folderNames.Reposition();
                SaveStoredPositions();
            }

            _dragMoved = false;
        }

        /// <summary>
        /// Shifts a node - or every project in a folder box - by <paramref name="delta"/>, and
        /// redraws the references that touch what moved.
        /// </summary>
        private void MoveBy(VNode vnode, Microsoft.Msagl.Core.Geometry.Point delta)
        {
            var moved = GraphDrag.Move(_graph, vnode.Node, delta);

            // Every element whose geometry moved has to be invalidated, not just the one under
            // the pointer - a folder box carries the projects inside it.
            foreach (var node in moved.Nodes) Invalidate(node);
            foreach (var edge in moved.Edges) InvalidateEdge(edge);

            _viewer.Invalidate(vnode);
            Remember(vnode.Node, delta);
        }

        /// <summary>
        /// Where the user has moved things to, as an offset from wherever the layout puts them,
        /// keyed by node id - a project's key, or a solution folder's name for a folder box.
        /// </summary>
        /// <remarks>
        /// An offset rather than an absolute position on purpose. Every rebuild is a fresh layout,
        /// and a stored coordinate from the previous one means nothing in the new one: change the
        /// direction from top-to-bottom to left-to-right and absolute positions would scatter the
        /// graph. An offset keeps "I moved this box out of the way" true against whatever the
        /// layout does next.
        /// </remarks>
        private readonly Dictionary<string, Microsoft.Msagl.Core.Geometry.Point> _dragOffsets =
            new Dictionary<string, Microsoft.Msagl.Core.Geometry.Point>();

        /// <summary>The solution the offsets in <see cref="_dragOffsets"/> belong to.</summary>
        private string _solutionPath;

        private void Remember(MsaglNode node, Microsoft.Msagl.Core.Geometry.Point delta)
        {
            string id = node?.Id;
            if (string.IsNullOrEmpty(id)) return;

            _dragOffsets.TryGetValue(id, out var offset);
            _dragOffsets[id] = offset + delta;
        }

        /// <summary>
        /// Reads back the offsets stored for this solution, when it is not the one already loaded.
        /// </summary>
        private void LoadStoredPositions(string solutionPath)
        {
            if (string.Equals(_solutionPath, solutionPath, StringComparison.OrdinalIgnoreCase))
                return;

            _solutionPath = solutionPath;
            _dragOffsets.Clear();

            foreach (var pair in State.NodePositions.Load(solutionPath))
                _dragOffsets[pair.Key] = pair.Value;
        }

        /// <summary>
        /// Writes the offsets out, so an arrangement survives closing the solution.
        /// </summary>
        /// <remarks>
        /// On dropping a node rather than on a timer or at shut-down: it is a few hundred bytes,
        /// and there is no reliable moment later - a tool window is not told the IDE is closing.
        /// </remarks>
        private void SaveStoredPositions()
        {
            if (_solutionPath == null) return;
            State.NodePositions.Save(_solutionPath, _dragOffsets);
        }

        /// <summary>
        /// Re-applies what the user dragged, after a rebuild has laid the graph out afresh.
        /// </summary>
        /// <remarks>
        /// An offset for something not in this graph - a project excluded by the depth limit, a
        /// folder box while grouping is off - is kept rather than dropped, so it comes back with
        /// the node.
        /// </remarks>
        private void ApplyStoredPositions()
        {
            if (_graph == null || _dragOffsets.Count == 0) return;

            foreach (var pair in _dragOffsets)
            {
                var node = FindNodeOrBox(pair.Key);
                if (node?.GeometryNode == null) continue;

                var moved = GraphDrag.Move(_graph, node, pair.Value);
                foreach (var n in moved.Nodes) Invalidate(n);
                foreach (var e in moved.Edges) InvalidateEdge(e);
            }

            _folderNames.Reposition();
        }

        /// <summary>A project node by its key, or a folder box by its folder name.</summary>
        private MsaglNode FindNodeOrBox(string id)
        {
            var node = _graph.FindNode(id);
            if (node != null) return node;

            if (_graph.RootSubgraph == null) return null;
            foreach (var subgraph in _graph.RootSubgraph.AllSubgraphsDepthFirstExcludingSelf())
                if (subgraph.Id == id) return subgraph;

            return null;
        }

        /// <summary>
        /// Puts everything back where the layout wants it.
        /// </summary>
        /// <remarks>
        /// Rebuilds even with nothing dragged, rather than returning early. Pressing a button and
        /// having it do nothing at all reads as broken, and re-running the layout is exactly what
        /// the button says it does.
        /// </remarks>
        private void ResetPositions()
        {
            bool had = _dragOffsets.Count > 0;

            _dragOffsets.Clear();
            if (had) SaveStoredPositions();

            Rebuild();
        }

        /// <summary>
        /// Drawing edge to its visual, built once per graph - as <see cref="_visuals"/>.
        /// </summary>
        /// <remarks>
        /// Typed as the interface because the WPF viewer's own VEdge class is internal to MSAGL,
        /// unlike VNode.
        /// </remarks>
        private Dictionary<Microsoft.Msagl.Drawing.Edge, Microsoft.Msagl.Drawing.IViewerEdge> _edgeVisuals;

        private void InvalidateEdge(Microsoft.Msagl.Drawing.Edge edge)
        {
            if (_edgeVisuals == null)
            {
                _edgeVisuals = new Dictionary<Microsoft.Msagl.Drawing.Edge, Microsoft.Msagl.Drawing.IViewerEdge>();
                foreach (var vedge in _viewer.Entities.OfType<Microsoft.Msagl.Drawing.IViewerEdge>())
                    _edgeVisuals[vedge.Edge] = vedge;
            }

            if (_edgeVisuals.TryGetValue(edge, out var visual)) _viewer.Invalidate(visual);
        }

        #endregion

        /// <summary>Solution folders currently drawn as a single node.</summary>
        private readonly HashSet<string> _collapsedFolders = new HashSet<string>();

        /// <summary>
        /// Listens for the collapse button on each folder box.
        /// </summary>
        /// <remarks>
        /// MSAGL draws the button and raises the event, but does nothing else - it flips
        /// Cluster.IsCollapsed and leaves the layout exactly as it was, so on its own the button
        /// looks broken. Collapsing is done here instead: the folder is remembered and the graph
        /// rebuilt with that folder as a single node, which is a real layout rather than a flag.
        /// </remarks>
        private void WatchForFolderCollapse()
        {
            foreach (var entity in _viewer.Entities)
            {
                var vnode = entity as VNode;
                if (!(vnode?.Node is Subgraph subgraph)) continue;

                string folder = subgraph.Id;
                vnode.IsCollapsedChanged += _ => CollapseFolder(folder);
            }
        }

        private void CollapseFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !_collapsedFolders.Add(folder)) return;
            Rebuild();
        }

        private void ExpandFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !_collapsedFolders.Remove(folder)) return;
            Rebuild();
        }

        /// <summary>Every node currently selected. Ctrl+click adds to it.</summary>
        private readonly HashSet<MsaglNode> _selectedNodes = new HashSet<MsaglNode>();

        /// <summary>Projects the graph is narrowed to, empty when not focused.</summary>
        private readonly HashSet<string> _focusKeys = new HashSet<string>();

        /// <summary>
        /// Narrows the graph to the selected projects. Does nothing without a selection, since
        /// focusing on nothing would just empty the window.
        /// </summary>
        private void ApplyFocus()
        {
            var keys = _selectedNodes
                .Select(n => GraphBuilder.ProjectOf(n)?.Key)
                .Where(k => k != null)
                .ToList();

            if (keys.Count == 0)
            {
                Btn_Focus.IsChecked = false;
                return;
            }

            _focusKeys.Clear();
            foreach (var key in keys) _focusKeys.Add(key);
            Rebuild();
        }

        private void Select(MsaglNode node, bool additive = false)
        {
            if (!additive) _selectedNodes.Clear();

            if (node != null)
            {
                if (additive && _selectedNodes.Contains(node)) _selectedNodes.Remove(node);
                else _selectedNodes.Add(node);
            }

            // Last one clicked, which is what the context menu acts on.
            _selected = _selectedNodes.Contains(node) ? node : _selectedNodes.FirstOrDefault();

            ResetHighlights();

            foreach (var selected in _selectedNodes)
            {
                selected.Attr.Color = _palette.SelectedStroke;
                selected.Attr.LineWidth = 3;
                Invalidate(selected);
            }

            ShowSelectionDetail();
        }

        private void ShowSelectionDetail()
        {
            if (_selectedNodes.Count == 0)
            {
                SB_Selection.Inlines.Clear();
                return;
            }

            if (_selectedNodes.Count > 1)
            {
                SetStat(SB_Selection, "Selected",
                    $"{_selectedNodes.Count} projects  (Focus draws only these)");
                return;
            }

            var project = GraphBuilder.ProjectOf(_selected);
            if (project == null)
            {
                SetStat(SB_Selection, "Selected", _selected?.LabelText ?? string.Empty);
                return;
            }

            // The key is the solution-relative project path, which is worth showing: the graph
            // only has room for short names, and it is what tells two same-named projects apart.
            string detail = $"{project.ProjectName}  (references {project.ReferencesCount}, " +
                            $"referenced by {project.ReferencedByCount})";
            if (project.Key != project.ProjectName) detail += $"  ·  {project.Key}";

            SetStat(SB_Selection, "Selected", detail);
        }

        /// <summary>
        /// Repaints every node in its resting colours. GraphViewer.Invalidate() with no argument
        /// is an empty method in this version, so each node has to be invalidated by hand
        /// through its VNode.
        /// </summary>
        private void ResetHighlights()
        {
            if (_graph == null) return;

            foreach (var node in _graph.Nodes)
            {
                var project = GraphBuilder.ProjectOf(node);
                bool isRoot = project != null && project.DepthLevel == 0;

                node.Attr.FillColor  = isRoot ? _palette.RootFill : _palette.NodeFill;
                node.Attr.Color      = isRoot ? _palette.RootStroke : _palette.NodeStroke;
                node.Label.FontColor = isRoot ? _palette.RootText : _palette.NodeText;
                node.Attr.LineWidth  = 1;

                Invalidate(node);
            }
        }

        /// <summary>
        /// Drawing node to its visual, built once per graph. Scanning GraphViewer.Entities per
        /// node would make repainting the whole graph quadratic.
        /// </summary>
        private Dictionary<MsaglNode, VNode> _visuals;

        private void Invalidate(MsaglNode node)
        {
            if (_visuals == null)
            {
                _visuals = new Dictionary<MsaglNode, VNode>();
                foreach (var vnode in _viewer.Entities.OfType<VNode>())
                    _visuals[vnode.Node] = vnode;
            }

            if (_visuals.TryGetValue(node, out var visual)) _viewer.Invalidate(visual);
        }

        private void Recolour(ProjectReferenceTree project, Color fill)
        {
            if (project == null || _graph == null) return;

            var node = _graph.FindNode(project.Key);
            if (node == null) return;

            node.Attr.FillColor = fill;
            node.Label.FontColor = _palette.NodeText;
            Invalidate(node);
        }

        private void HighlightReferencing()
        {
            var project = GraphBuilder.ProjectOf(_selected);
            if (project == null) return;

            Select(_selected);
            project.WithEach(other =>
            {
                if (other.References.Contains(project)) Recolour(other, _palette.ReferencingFill);
            });
        }

        private void HighlightReferenced()
        {
            var project = GraphBuilder.ProjectOf(_selected);
            if (project == null) return;

            Select(_selected);
            foreach (var reference in project.References)
                Recolour(reference, _palette.ReferencedFill);
        }

        private void ShowProject(MsaglNode node)
        {
            var project = GraphBuilder.ProjectOf(node);
            if (project == null) return;

            // Matched on the key, so drilling in lands on the right project even when two
            // share a name.
            TrySelectByKey(project.Key);
        }

        private void BuildNodeContextMenu()
        {
            var menu = new ContextMenu();

            var referencing = new MenuItem { Header = "Highlight referencing projects" };
            referencing.Click += (s, e) => HighlightReferencing();

            var referenced = new MenuItem { Header = "Highlight referenced projects" };
            referenced.Click += (s, e) => HighlightReferenced();

            var drillIn = new MenuItem { Header = "Show this project's reference graph" };
            drillIn.Click += (s, e) => ShowProject(_selected);

            var clear = new MenuItem { Header = "Clear highlighting" };
            clear.Click += (s, e) => Select(null);

            var reset = new MenuItem { Header = "Reset dragged positions" };
            reset.Click += (s, e) => ResetPositions();

            var nodeItems = new Separator();
            var resetItems = new Separator();

            menu.Items.Add(referencing);
            menu.Items.Add(referenced);
            menu.Items.Add(nodeItems);
            menu.Items.Add(drillIn);
            menu.Items.Add(resetItems);
            menu.Items.Add(clear);
            menu.Items.Add(reset);

            GraphHost.ContextMenu = menu;

            // Most of the menu is about a particular node, so select what was right-clicked and
            // hide those entries when the click landed on empty canvas - where the only thing
            // worth offering is putting a dragged layout back.
            GraphHost.ContextMenuOpening += (s, e) =>
            {
                var node = NodeUnderCursor();

                var forNode = node == null ? Visibility.Collapsed : Visibility.Visible;
                referencing.Visibility = forNode;
                referenced.Visibility  = forNode;
                drillIn.Visibility     = forNode;
                clear.Visibility       = forNode;
                nodeItems.Visibility   = forNode;
                resetItems.Visibility  = forNode;

                reset.IsEnabled = _dragOffsets.Count > 0;
                reset.Header = _dragOffsets.Count == 1
                    ? "Reset the dragged position"
                    : $"Reset {_dragOffsets.Count} dragged positions";
                if (_dragOffsets.Count == 0) reset.Header = "Reset dragged positions";

                if (node == null) return;

                Select(node);
                referencing.Header = $"Highlight projects referencing '{node.LabelText}'";
                referenced.Header  = $"Highlight projects referenced by '{node.LabelText}'";
                drillIn.Header     = $"Show the reference graph of '{node.LabelText}'";
            };
        }

        #endregion

        #region Saving

        private void Btn_SaveAs_Click(object sender, RoutedEventArgs e)
        {
            if (_prt == null) return;

            string filter = "PNG Image File (*.png)    |*.png|" +   // FilterIndex - 1
                            "DGML Files (*.dgml)       |*.dgml|" +  // FilterIndex - 2
                            "Graphviz DOT File (*.dot) |*.dot";     // FilterIndex - 3
            if (OutputHelper.IsGraphvizAvailable())
            {
                filter += "|Graphviz SVG File (*.svg)|*.svg" +      // FilterIndex - 4
                          "|Graphviz PNG File (*.png)|*.png";       // FilterIndex - 5
            }

            var dialog = new SaveFileDialog
            {
                FileName = (SelectedProject?.Name ?? "References") + ".png",
                Filter   = filter,
                Title    = "Save Project Dependency Graph As"
            };

            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            bool saved;
            string caption;

            switch (dialog.FilterIndex)
            {
                case 1:
                    saved = SaveGraphImage(dialog.FileName);
                    caption = "Create PNG";
                    break;
                case 2:
                    saved = OutputHelper.CreateProjectDGML(_prt, dialog.FileName);
                    caption = "Create Project DGML";
                    break;
                case 3:
                    saved = OutputHelper.CreateGraphvizDot(_prt, dialog.FileName);
                    caption = "Create Project Graphviz Dot File";
                    break;
                case 4:
                    saved = OutputHelper.CreateGraphvizSVG(_prt, dialog.FileName);
                    caption = "Create Project Graphviz SVG File";
                    break;
                case 5:
                    saved = OutputHelper.CreateGraphvizPNG(_prt, dialog.FileName);
                    caption = "Create Project Graphviz PNG File";
                    break;
                default:
                    return;
            }

            ReportSaveResult(saved, dialog.FileName, caption);
        }

        /// <summary>
        /// Saves the whole graph, not just the part currently scrolled into view.
        /// </summary>
        private bool SaveGraphImage(string fileName)
        {
            if (_viewer.GraphCanvas == null) return false;

            var bounds = GraphBuilder.ExportBounds(_graph, _viewer);

            if (fileName == null)
            {
                return ImageSaver.AsPNG(_viewer.GraphCanvas, bounds,
                    SelectedProject?.Name ?? "References");
            }

            return ImageSaver.AsPNG(_viewer.GraphCanvas, fileName, bounds);
        }

        private void Btn_SaveSolutionDGML_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_vsScanner == null) return;

            string filter = "DGML Files (*.dgml)       |*.dgml|" +  // FilterIndex - 1
                            "Graphviz DOT File (*.dot) |*.dot";     // FilterIndex - 2
            if (OutputHelper.IsGraphvizAvailable())
            {
                filter += "|Graphviz SVG File (*.svg)|*.svg" +      // FilterIndex - 3
                          "|Graphviz PNG File (*.png)|*.png";       // FilterIndex - 4
            }

            var dialog = new SaveFileDialog
            {
                FileName = Path.GetFileNameWithoutExtension(_vsScanner._dte.Solution.FullName) + ".dgml",
                Filter   = filter,
                Title    = "Save Solution Dependency Graph As"
            };

            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            bool saved;
            string caption;

            switch (dialog.FilterIndex)
            {
                case 1:
                    saved = OutputHelper.CreateSolutionDGML(_vsScanner, dialog.FileName);
                    caption = "Create Solution DGML File";
                    break;
                case 2:
                    saved = OutputHelper.CreateSolutionGraphvizDot(_vsScanner, dialog.FileName);
                    caption = "Create Solution Graphviz Dot File";
                    break;
                case 3:
                    saved = OutputHelper.CreateSolutionGraphvizSVG(_vsScanner, dialog.FileName);
                    caption = "Create Solution Graphviz SVG File";
                    break;
                case 4:
                    saved = OutputHelper.CreateSolutionGraphvizPNG(_vsScanner, dialog.FileName);
                    caption = "Create Solution Graphviz PNG File";
                    break;
                default:
                    return;
            }

            ReportSaveResult(saved, dialog.FileName, caption);
        }

        private static void ReportSaveResult(bool saved, string fileName, string caption)
        {
            string message = saved
                ? $"Output file '{fileName}' has been created."
                : $"Failed to create output file '{fileName}'.";

            System.Windows.MessageBox.Show(message, caption, MessageBoxButton.OK,
                saved ? MessageBoxImage.Information : MessageBoxImage.Error);
        }

        #endregion
    }
}
