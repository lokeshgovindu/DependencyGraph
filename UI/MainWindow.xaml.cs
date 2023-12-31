﻿using DependencyGraph.Scan;
using DependencyGraph.UI;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell.Interop;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;

namespace DependencyGraph
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : System.Windows.Window, INotifyPropertyChanged
    {
        private ProjectsNodesView   _ns;
        private _DTE                _dte;
        private VSScanner           _vsScanner;
        private string              _statusBarText;

        public MainWindow(DTE2 dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            InitializeComponent();

            // To avoid showing in Alt+Tab
            this.Owner = System.Windows.Application.Current.MainWindow;

            // Set the DataContext to the current window
            DataContext = this;

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

        public string StatusBarText
        {
            get { return _statusBarText; }
            set
            {
                if (_statusBarText != value)
                {
                    _statusBarText = value;
                    OnPropertyChanged(nameof(StatusBarText));
                }
            }
        }

        private void OnProjectShowRequest(string projectName)
        {
            CBProjectNames.SelectedItem = projectName;
        }

        private const string ShowDeepestReferencesOnly = "Show Deepest References Only";
        private const string ShowAllReferences         = "Show All References";
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

            Btn_SaveAs.Click            += Btn_SaveAs_Click;
            Btn_SaveSolutionDGML.Click  += Btn_SaveSolutionDGML_Click;

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
                    UpdateStatusBarText(projectName);
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
            string filter = "PNG Image File (*.png)    |*.png|" +   // FilterIndex - 1
                            "DGML Files (*.dgml)       |*.dgml|" +  // FilterIndex - 2
                            "Graphviz DOT File (*.dot) |*.dot";     // FilterIndex - 3
            if (ExistsOnPath("dot.exe"))
            {
                filter += "|Graphviz SVG File (*.svg)|*.svg" +      // FilterIndex - 4
                          "|Graphviz PNG File (*.png)|*.png";       // FilterIndex - 5
            }

            SaveFileDialog saveFileDialog = new SaveFileDialog
            {
                InitialDirectory = null,
                FileName         = CBProjectNames.SelectedItem.ToString() + ".png",
                Filter           = filter,
                Title            = "Save Project Dependency Graph As"
            };

            if (saveFileDialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return;

            bool saved = false;
            string caption = string.Empty;

            switch (saveFileDialog.FilterIndex)
            {
                case 1:
                    saved = ImageSaver.AsPNG(_ns.View, saveFileDialog.FileName);
                    caption = "Create PNG";
                    break;

                case 2:
                    saved = OutputHelper.CreateProjectDGML(_ns.PRT, saveFileDialog.FileName);
                    caption = "Create Project DGML";
                    break;

                case 3:
                    saved = OutputHelper.CreateGraphvizDot(_ns.PRT, saveFileDialog.FileName);
                    caption = "Create Project Graphviz Dot File";
                    break;

                case 4:
                    saved = OutputHelper.CreateGraphvizSVG(_ns.PRT, saveFileDialog.FileName);
                    caption = "Create Project Graphviz SVG File";
                    break;

                case 5:
                    saved = OutputHelper.CreateGraphvizPNG(_ns.PRT, saveFileDialog.FileName);
                    caption = "Create Project Graphviz PNG File";
                    break;

                default:
                    break;
            }

            string message;
            MessageBoxImage icon = MessageBoxImage.Information;
            if (saved)
            {
                message = string.Format("Output file '{0}' has been created.", saveFileDialog.FileName);
            }
            else
            {
                message = string.Format("Failed to create output file '{0}'.", saveFileDialog.FileName);
                icon = MessageBoxImage.Error;
            }

            System.Windows.MessageBox.Show(message, caption, MessageBoxButton.OK, icon);
        }

        public static bool ExistsOnPath(string fileName)
        {
            return GetFullPath(fileName) != null;
        }

        public static string GetFullPath(string fileName)
        {
            if (File.Exists(fileName))
                return Path.GetFullPath(fileName);

            var values = Environment.GetEnvironmentVariable("PATH");
            foreach (var path in values.Split(Path.PathSeparator))
            {
                var fullPath = Path.Combine(path, fileName);
                if (File.Exists(fullPath))
                    return fullPath;
            }
            return null;
        }

        private void Btn_SaveSolutionDGML_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string fileName = Path.GetFileNameWithoutExtension(_vsScanner._dte.Solution.FullName);

            string filter = "DGML Files (*.dgml)       |*.dgml|" +  // FilterIndex - 1
                            "Graphviz DOT File (*.dot) |*.dot";     // FilterIndex - 2
            if (ExistsOnPath("dot.exe"))
            {
                filter += "|Graphviz SVG File (*.svg)|*.svg" +      // FilterIndex - 3
                          "|Graphviz PNG File (*.png)|*.png";       // FilterIndex - 4
            }

            SaveFileDialog saveFileDialog = new SaveFileDialog
            {
                InitialDirectory = null,
                FileName         = fileName + ".dgml",
                Filter           = filter,
                Title            = "Save Solution Dependency Graph As"

            };

            if (saveFileDialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            {
                return;
            }

            bool saved = false;
            string caption = string.Empty;
            switch (saveFileDialog.FilterIndex)
            {
                case 1:
                    saved = OutputHelper.CreateSolutionDGML(_vsScanner, saveFileDialog.FileName);
                    caption = "Create Solution DGML File";
                    break;

                case 2:
                    saved = OutputHelper.CreateSolutionGraphvizDot(_vsScanner, saveFileDialog.FileName);
                    caption = "Create Solution Graphviz Dot File";
                    break;

                case 3:
                    saved = OutputHelper.CreateSolutionGraphvizSVG(_vsScanner, saveFileDialog.FileName);
                    caption = "Create Solution Graphviz SVG File";
                    break;

                case 4:
                    saved = OutputHelper.CreateSolutionGraphvizPNG(_vsScanner, saveFileDialog.FileName);
                    caption = "Create Solution Graphviz PNG File";
                    break;

                default:
                    break;
            }

            string message;
            MessageBoxImage icon = MessageBoxImage.Information;
            if (saved)
            {
                message = string.Format("Output file '{0}' has been created.", saveFileDialog.FileName);
            }
            else
            {
                message = string.Format("Failed to create output file '{0}'.", saveFileDialog.FileName);
                icon = MessageBoxImage.Error;
            }

            System.Windows.MessageBox.Show(message, caption, MessageBoxButton.OK, icon);
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
            UpdateStatusBarText(projectName);
            //_ns.ShowConnections();
        }

        private void UpdateStatusBarText(string projectName)
        {
            //StatusBarText = String.Format("ProjectName: {0}    Nodes: {1}    Connections: {2}", projectName, _ns.NodesCount, _ns.ConnectionsCount);
            SB_ProjectName.Text   = String.Format("ProjectName: {0}", projectName);
            SB_Nodes.Text         = String.Format("Nodes: {0}", _ns.NodesCount);
            SB_Connections.Text   = String.Format("Connections: {0}", _ns.ConnectionsCount);
            SB_ProjectsCount.Text = String.Format("#Projects in Solution: {0}", _vsScanner.Projects.Count);
        }

        // Implement INotifyPropertyChanged to notify the UI of property changes
        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
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
