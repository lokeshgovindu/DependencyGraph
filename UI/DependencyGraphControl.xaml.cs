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

        private readonly Dictionary<string, ConnectionsVisibility> _displayMethods =
            new Dictionary<string, ConnectionsVisibility>
            {
                { ShowDeepestReferencesOnly, ConnectionsVisibility.Deepest },
                { ShowAllReferences,         ConnectionsVisibility.All     },
                { ShowProjectReferencesOnly, ConnectionsVisibility.Project },
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

        public DependencyGraphControl()
        {
            InitializeComponent();

            _palette = GraphPalette.ForVsTheme();
            GraphHost.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(
                    _palette.Background.R, _palette.Background.G, _palette.Background.B));
            ApplyThemeToLabels();

            _viewer.LayoutEditingEnabled = false;
            _viewer.RunLayoutAsync = false;
            _viewer.BindToPanel(GraphHost);
            _viewer.MouseDown += Viewer_MouseDown;
            _viewer.ViewChangeEvent += (s, e) => UpdateZoomIndicator();
            GraphHost.SizeChanged += GraphHost_SizeChanged;

            BuildNodeContextMenu();
            PopulateFixedControls();

            Btn_ZoomFit.Click           += (s, e) => { _viewer.SetInitialTransform(); UpdateZoomIndicator(); };
            Btn_Refresh.Click           += (s, e) => Rescan();
            Btn_SaveAs.Click            += Btn_SaveAs_Click;
            Btn_SaveSolutionDGML.Click  += Btn_SaveSolutionDGML_Click;

            CBProjectNames.SelectionChanged  += (s, e) => Rebuild();
            CBDisplayMethod.SelectionChanged += (s, e) => Rebuild();
            CBOrientation.SelectionChanged   += (s, e) => Rebuild();

            PreviewKeyUp += (s, e) =>
            {
                if (e.Key == Key.S && (Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
                    SaveGraphImage(null);
            };
        }

        #region Setup

        /// <summary>
        /// Colours the toolbar and status bar text to suit the active theme.
        /// </summary>
        /// <remarks>
        /// Taken from the graph palette rather than a shell colour key. Two earlier attempts
        /// failed: the XAML's DynamicResource lookup of the shell brush silently found nothing
        /// (those keys are not in this element's resource tree), and VSColorTheme.GetThemedColor
        /// returns a colour whose alpha is zero, so building a brush from it painted the text
        /// fully transparent. The palette already knows whether the theme is light or dark and its
        /// text colour is opaque by construction.
        /// </remarks>
        private void ApplyThemeToLabels()
        {
            var text = _palette.NodeText;
            var brush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(text.R, text.G, text.B));

            // ToolBar and StatusBar default to WPF's light system chrome no matter what the
            // shell theme is, so the containers have to be painted too - otherwise light text
            // lands on a light strip and disappears.
            var back = _palette.Background;
            var backBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(back.R, back.G, back.B));

            Tray.Background           = backBrush;
            MainToolBar.Background    = backBrush;
            StatusBarMain.Background  = backBrush;
            StatusBarMain.Foreground  = brush;

            Lbl_Project.Foreground   = brush;
            Lbl_Show.Foreground      = brush;
            Lbl_Direction.Foreground = brush;

            // ToolBar restyles its buttons too, which left these looking greyed out.
            foreach (var button in new[] { Btn_ZoomFit, Btn_Refresh, Btn_SaveAs, Btn_SaveSolutionDGML })
            {
                button.Foreground = brush;
                button.Background = backBrush;
            }

            foreach (var combo in new[] { CBProjectNames, CBDisplayMethod, CBOrientation })
            {
                combo.Foreground = brush;
                combo.Background = backBrush;

                // A string item is rendered by a TextBlock the ContentPresenter creates, and that
                // does not inherit the ComboBox's Foreground through ToolBar's template - which
                // left the selected text almost invisible. An implicit style reaches both the
                // closed combo and its drop-down list.
                var itemText = new System.Windows.Style(typeof(TextBlock));
                itemText.Setters.Add(new System.Windows.Setter(TextBlock.ForegroundProperty, brush));
                combo.Resources[typeof(TextBlock)] = itemText;
            }

            SB_ProjectName.Foreground   = brush;
            SB_Nodes.Foreground         = brush;
            SB_Connections.Foreground   = brush;
            SB_Selection.Foreground     = brush;
            SB_Zoom.Foreground          = brush;
            SB_ProjectsCount.Foreground = brush;
            EmptyMessage.Foreground     = brush;
        }

        private void PopulateFixedControls()
        {
            _loading = true;

            foreach (var name in _displayMethods.Keys) CBDisplayMethod.Items.Add(name);
            CBDisplayMethod.SelectedIndex = 0;

            foreach (var name in _directions.Keys) CBOrientation.Items.Add(name);
            CBOrientation.SelectedIndex = 0;

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
        /// Frames the graph at 100% zoom, centred on the project being examined.
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

            var anchor = _graph.Nodes.FirstOrDefault(n => GraphBuilder.ProjectOf(n)?.DepthLevel == 0)
                      ?? _graph.Nodes.FirstOrDefault();

            if (anchor?.GeometryNode != null) _viewer.NodeToCenterWithScale(anchor, 1.0);
            else _viewer.SetInitialTransform();

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
            string previous = CBProjectNames.SelectedItem as string;
            CBProjectNames.Items.Clear();
            foreach (var project in _vsScanner.Projects)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                CBProjectNames.Items.Add(project.Name);
            }
            _loading = false;

            HideEmptyMessage();
            SelectInitialProject(previous);
        }

        private void SelectInitialProject(string previous)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (previous != null && CBProjectNames.Items.Contains(previous))
            {
                CBProjectNames.SelectedItem = previous;
                return;
            }

            var activeProjects = _dte.ActiveSolutionProjects as Array;
            if (activeProjects != null && activeProjects.Length > 0)
            {
                var active = activeProjects.GetValue(0) as EnvDTE.Project;
                if (active != null && CBProjectNames.Items.Contains(active.Name))
                {
                    CBProjectNames.SelectedItem = active.Name;
                    return;
                }
            }

            if (_vsScanner.StartupProject != null
                && CBProjectNames.Items.Contains(_vsScanner.StartupProject.Name))
            {
                CBProjectNames.SelectedItem = _vsScanner.StartupProject.Name;
                return;
            }

            CBProjectNames.SelectedIndex = 0;
        }

        private void ShowEmptyMessage(string message)
        {
            EmptyMessage.Text = message;
            EmptyMessage.Visibility = Visibility.Visible;
            GraphHost.Visibility = Visibility.Collapsed;
            SB_ProjectName.Text = SB_Nodes.Text = SB_Connections.Text = string.Empty;
            SB_Selection.Text = SB_ProjectsCount.Text = string.Empty;
        }

        private void HideEmptyMessage()
        {
            EmptyMessage.Visibility = Visibility.Collapsed;
            GraphHost.Visibility = Visibility.Visible;
        }

        #endregion

        #region Building the graph

        private ConnectionsVisibility SelectedVisibility
            => _displayMethods[(string)CBDisplayMethod.SelectedItem];

        private LayerDirection SelectedDirection
            => _directions[(string)CBOrientation.SelectedItem];

        private void Rebuild()
        {
            if (_loading || _vsScanner == null) return;

            string projectName = CBProjectNames.SelectedItem as string;
            if (projectName == null) return;

            ThreadHelper.ThrowIfNotOnUIThread();

            var visibility = SelectedVisibility;
            try
            {
                _prt = visibility == ConnectionsVisibility.Project
                    ? _vsScanner.GetProjectReferencesOnly(projectName)
                    : _vsScanner.GetProjectReferenceTree(projectName);
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

            _selected = null;
            _visuals = null;
            _graph = GraphBuilder.Build(_prt, visibility, SelectedDirection, _palette);
            _viewer.Graph = _graph;

            // Frame the new graph now if the panel is measured, otherwise leave it to
            // GraphHost_SizeChanged.
            _hasBeenFitted = GraphHost.ActualWidth > 0 && GraphHost.ActualHeight > 0;
            if (_hasBeenFitted) ShowAtActualSize();

            UpdateStatusBar(projectName);
        }

        private void UpdateStatusBar(string projectName)
        {
            SB_ProjectName.Text   = $"Project: {projectName}";
            SB_Nodes.Text         = $"Nodes: {_graph.NodeCount}";
            SB_Connections.Text   = $"Connections: {_graph.EdgeCount}";
            SB_ProjectsCount.Text = $"Projects in solution: {_vsScanner.Projects.Count}";
            SB_Selection.Text     = string.Empty;
            UpdateZoomIndicator();
        }

        /// <summary>
        /// Shows the current zoom as a percentage, where 100% is the graph at its natural size.
        /// </summary>
        private void UpdateZoomIndicator()
        {
            if (_graph == null) { SB_Zoom.Text = string.Empty; return; }

            try
            {
                // CurrentScale reads the canvas's render transform, which is not there before
                // the first graph has been laid out.
                SB_Zoom.Text = $"Zoom: {_viewer.CurrentScale * 100:0}%";
            }
            catch
            {
                SB_Zoom.Text = string.Empty;
            }
        }

        #endregion

        #region Selection, highlighting and the node context menu

        private void Viewer_MouseDown(object sender, Microsoft.Msagl.Drawing.MsaglMouseEventArgs e)
        {
            var node = NodeUnderCursor();

            if (e.Clicks > 1 && node != null)
            {
                // Double click drills into that project's own reference tree.
                ShowProject(node);
                return;
            }

            Select(node);
        }

        private MsaglNode NodeUnderCursor()
        {
            return (_viewer.ObjectUnderMouseCursor as VNode)?.Node;
        }

        private void Select(MsaglNode node)
        {
            _selected = node;
            ResetHighlights();

            if (node == null)
            {
                SB_Selection.Text = string.Empty;
                return;
            }

            node.Attr.Color = _palette.SelectedStroke;
            node.Attr.LineWidth = 3;
            Invalidate(node);

            var project = GraphBuilder.ProjectOf(node);
            SB_Selection.Text = project == null
                ? $"Selected: {node.LabelText}"
                : $"Selected: {project.ProjectName}  (references {project.ReferencesCount}, " +
                  $"referenced by {project.ReferencedByCount})";
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

            var node = _graph.FindNode(project.ProjectName);
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
            if (!CBProjectNames.Items.Contains(project.ProjectName)) return;

            CBProjectNames.SelectedItem = project.ProjectName;
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

            menu.Items.Add(referencing);
            menu.Items.Add(referenced);
            menu.Items.Add(new Separator());
            menu.Items.Add(drillIn);
            menu.Items.Add(new Separator());
            menu.Items.Add(clear);

            GraphHost.ContextMenu = menu;

            // The menu only makes sense over a node, so select what was right-clicked first and
            // suppress the menu entirely when the click landed on empty canvas.
            GraphHost.ContextMenuOpening += (s, e) =>
            {
                var node = NodeUnderCursor();
                if (node == null) { e.Handled = true; return; }

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
                FileName = CBProjectNames.SelectedItem + ".png",
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
                    CBProjectNames.SelectedItem as string ?? "References");
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
