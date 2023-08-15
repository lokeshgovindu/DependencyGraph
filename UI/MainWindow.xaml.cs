using EnvDTE;
using Microsoft.VisualStudio.Package;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Xml.Linq;
using VSLangProj;
using DependencyGraph.Scan;
using DependencyGraph.UI;
using EnvDTE80;
using Project = EnvDTE.Project;
using DependencyGraph.Properties;

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
            SetupContols();
            WindowState = WindowState.Maximized;
            _dte = dte;
            _vsScanner = new VSScanner(_dte);
            foreach (var project in _vsScanner.Projects)
            {
                CBProjectNames.Items.Add(project.Name);
            }
            Display();
        }

        public void Display()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            CBProjectNames.SelectedItem = _vsScanner.StartupProject.Name;
            //var activeProject = _dte.ActiveSolutionProjects as Project;
            //CBProjectNames.SelectedItem = activeProject.Name;
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
                if (!_ns) return;
                if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
                {
                    if (e.Key == Key.S)
                        ImageSaver.AsPNG(_ns.View, "References");
                }
            };

            //ShowAllReferences.Click += (s, e) =>
            //{
            //    alternate(ShowAllReferences,
            //        ShowAllReferences,
            //        ShowDeepestReferencesOnly);
            //    if ((string)ShowAllReferences.Content == ShowAllReferences)
            //        _ns.DisplayMethod = ConnectionsVisibility.Deepest;
            //    else _ns.DisplayMethod = ConnectionsVisibility.All;
            //};

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
        public static T alternate<T>(T e, string s1, string s2) where T : FrameworkElement
        {
            if (e is Label l) l.Content = (string)l.Content == s1 ? s2 : s1;
            else if (e is Button b) b.Content = (string)b.Content == s1 ? s2 : s1;
            return e;
        }
        #endregion

    }
}
