using DependencyGraph.Scan;
using DependencyGraph.UI;
using EnvDTE;
using EnvDTE80;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;

namespace DependencyGraph
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : System.Windows.Window
    {
        private ProjectsNodesView _ns;
        private _DTE _dte;
        VSScanner _vsScanner;

        public MainWindow(DTE2 dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            InitializeComponent();

            // To avoid showing in Alt+Tab
            this.Owner = System.Windows.Application.Current.MainWindow;

            SetupContols();
            _dte = dte;
            _vsScanner = new VSScanner(_dte);
            foreach (var project in _vsScanner.Projects)
            {
                CBProjectNames.Items.Add(project.Name);
            }
            
            this.SourceInitialized += (x, y) => { this.HideMinimizeAndMaximizeButtons(); };

            Display();
        }

        public void Display()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var activeProjects = _dte.ActiveSolutionProjects as Array;
            if (activeProjects != null && activeProjects.Length > 0)
            {
                // Assuming there is only one active project
                EnvDTE.Project activeProject = activeProjects.GetValue(0) as EnvDTE.Project;
                if (activeProject != null)
                {
                    CBProjectNames.SelectedItem = activeProject.Name;
                }
            }
            else
            {
                CBProjectNames.SelectedItem = _vsScanner.StartupProject.Name;
            }
        }

        private void OnProjectShowRequest(string projectName)
        {
            CBProjectNames.SelectedItem = projectName;
        }

        private const string ShowDeepestReferencesOnly = "Show Deepest References Only";
        private const string ShowAllReferences = "Show All References";
        private const string ShowProjectReferencesOnly = "Show Project References Only";

        private Dictionary<string, ConnectionsVisibility> _displayMethods =
            new Dictionary<string, ConnectionsVisibility>() {
                { ShowDeepestReferencesOnly, ConnectionsVisibility.Deepest },
                { ShowAllReferences, ConnectionsVisibility.All },
                { ShowProjectReferencesOnly, ConnectionsVisibility.Project },
            };

        private void SetupContols()
        {
            CBDisplayMethod.Items.Add(ShowDeepestReferencesOnly);
            CBDisplayMethod.Items.Add(ShowAllReferences);
            CBDisplayMethod.Items.Add(ShowProjectReferencesOnly);
            CBDisplayMethod.SelectedIndex = 0;

            PreviewKeyUp += (s, e) =>
            {
                if (Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt))
                {
                    if (e.Key == Key.F4)
                    {
                        System.Windows.MessageBox.Show("Sorry you cannot close this form via ALT+F4");
                        Close();
                        e.Handled = true;
                    }
                }

                if (!_ns) return;

                if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
                {
                    if (e.Key == Key.S)
                        ImageSaver.AsPNG(_ns.View, "References");
                }
            };

            //this.Closing += (s, e) => { e.Cancel = true; };

            Btn_SaveAs.Click += Btn_SaveAs_Click;

            CBDisplayMethod.SelectionChanged += (s, e) =>
            {
                string prevDisplayMethodStr = (string)((object[])e.RemovedItems)[0];
                string projectName = CBProjectNames.SelectedItem.ToString();
                string displayMethodStr = CBDisplayMethod.SelectedItem.ToString();
                ConnectionsVisibility displayMethod = _displayMethods[displayMethodStr];
                ConnectionsVisibility displayMethodPrev = _displayMethods[prevDisplayMethodStr];

                if (displayMethodPrev != ConnectionsVisibility.Project && displayMethod != ConnectionsVisibility.Project)
                {
                    _ns.DisplayMethod = displayMethod;
                }
                else
                {
                    SelectionChangedHandler(projectName, displayMethod);
                }
            };

            CBProjectNames.SelectionChanged += (s, e) =>
            {
                if (CBProjectNames.SelectedItem != null)
                {
                    string projectName = CBProjectNames.SelectedItem.ToString();
                    ConnectionsVisibility displayMethod = _displayMethods[CBDisplayMethod.SelectedItem.ToString()];
                    SelectionChangedHandler(projectName, displayMethod);
                }
            };
        }

        private void Btn_SaveAs_Click(object sender, RoutedEventArgs e)
        {
            SaveFileDialog saveFileDialog = new SaveFileDialog
            {
                InitialDirectory = null,
                FileName = CBProjectNames.SelectedItem.ToString() + ".png",
                Filter = "PNG image file (*.png)|*.png"
            };

            if (saveFileDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                ImageSaver.SaveAsPNG(_ns.View, saveFileDialog.FileName);
            }
        }

        private void SelectionChangedHandler(string projectName, ConnectionsVisibility displayMethod)
        {
            Title = $@"{_vsScanner.SolutionName} - Project Reference Tree";
            _ns = new ProjectsNodesView();
            _ns.SHOW_PROJECT_REQUEST += OnProjectShowRequest;
            _ns.DisplayMethod = displayMethod;
            foreach (UIElement item in mainGrid.Children)
            {
                if (item is NodeNetwork.Views.NetworkView)
                {
                    mainGrid.Children.Remove(item);
                    break;
                }
            }
            mainGrid.Children.Insert(0, _ns.View);
            ProjectReferenceTree prt;
            if (_ns.DisplayMethod == ConnectionsVisibility.Project)
            {
                prt = _vsScanner.GetProjectReferencesOnly(projectName);
            }
            else
            {
                prt = _vsScanner.GetProjectReferenceTree(projectName);
            }
            if (!prt) { Close(); return; }
            _ns.PRT = prt;
            //_ns.ShowConnections();
        }

        #region Helper methods
        //public static T alternate<T>(T e, string s1, string s2) where T : FrameworkElement
        //{
        //    if (e is Label l) l.Content = (string)l.Content == s1 ? s2 : s1;
        //    else if (e is Button b) b.Content = (string)b.Content == s1 ? s2 : s1;
        //    return e;
        //}
        #endregion

    }

    internal static class WindowExtensions
    {
        // from winuser.h
        private const int GWL_STYLE = -16,
                          WS_MAXIMIZEBOX = 0x10000,
                          WS_MINIMIZEBOX = 0x20000;

        [DllImport("user32.dll")]
        extern private static int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        extern private static int SetWindowLong(IntPtr hwnd, int index, int value);

        internal static void HideMinimizeAndMaximizeButtons(this System.Windows.Window window)
        {
            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            var currentStyle = GetWindowLong(hwnd, GWL_STYLE);

            SetWindowLong(hwnd, GWL_STYLE, (currentStyle & ~WS_MINIMIZEBOX));
        }
    }
}
