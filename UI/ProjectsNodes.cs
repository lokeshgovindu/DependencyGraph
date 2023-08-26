using DynamicData;
using EnvDTE;
using NodeNetwork.ViewModels;
using NodeNetwork.Views;
using ReactiveUI;
using System;
using System.Collections.Generic;
//using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
//using System.Windows.Controls;
using System.Windows.Media;
using DependencyGraph.Scan;
using Project = EnvDTE.Project;

namespace DependencyGraph.UI
{
    public class ProjectsNodesView
    {
        public event Action<string> SHOW_PROJECT_REQUEST;

        /// <summary>Size of view (specified in constructor). This is only used for layout purposes.</summary>
        public (double w, double h) Size { get; }

        /// <summary>
        /// Estimate size of node for layout - I don't know how to get actual note size or even a node view.
        /// Actual width is simply <see cref="emptyNodeWidth"/> + <see cref="NodeSize"/> 
        /// width multiplied by number of characters in project's name.
        /// </summary>
        public (double w, double h) NodeSize { get; set; } = (10, 200);

        /// <summary>Estimated width for empty node (without name).</summary>
        public double emptyNodeWidth = 100;

        /// <summary>Outer WPF element</summary>
        public NetworkView View { get; }


        private ConnectionsVisibility _dispMeth = ConnectionsVisibility.Deepest;

        public ConnectionsVisibility DisplayMethod
        {
            get => _dispMeth;
            set
            {
                _dispMeth = value;
                ShowConnections();
            }
        }

        private ProjectReferenceTree _prt;
        /// <summary>Projects reference tree to be displayed.</summary>

        public ProjectReferenceTree PRT
        {
            get => _prt;
            set
            {
                //if (_rt) throw new Exception("Resetting reference tree is not supported");
                _prt = value;
                DisplayTree(_prt);
                ShowConnections();
            }
        }

        private NetworkViewModel _networkViewModel;
        public NetworkViewModel NVM => _networkViewModel;

        public ProjectsNodesView(double w = 800, double h = 600)
        {
            this.Size = (w, h);
            _networkViewModel = new NetworkViewModel();
            View = new NetworkView { ViewModel = _networkViewModel };
            SetDisplayMethods();
        }

        private readonly Dictionary<ProjectReferenceTree, RefContext> nodes = new Dictionary<ProjectReferenceTree, RefContext>();

        private void DisplayTree(ProjectReferenceTree prt)
        {
            int cl = prt.DepthLevel; var rol = prt.AllOnLevel(cl);
            var cx = 0d; var ln = 0;
            while (rol != null)
            {
                var nw = NodeSize.w * ln + emptyNodeWidth; //node width
                var nh = NodeSize.h;
                var th = rol.Count * nh; //total height
                var top = (Size.h - th) / 2;
                cx += nw;

                var i = 0; foreach (var r in rol)
                {
                    var nc = CreateRefContext(r);
                    if (cl == 0) nc.node.BackColor = Colors.DarkOliveGreen;
                    nc.node.Position = new Point(cx, top + nh * i++);
                }
                ln = rol.Max(sr =>
                {
                    Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
                    return sr.Item.Name.Length;
                });
                rol = prt.AllOnLevel(++cl);
            }
        }

        #region Connections

        public void ShowConnections()
        {
            _networkViewModel.Connections.Clear();
            displayMethods[DisplayMethod]();
        }

        private Dictionary<ConnectionsVisibility, Action> displayMethods;
        private void SetDisplayMethods()
        {
            displayMethods = new Dictionary<ConnectionsVisibility, Action>() {
                { ConnectionsVisibility.Deepest, ShowDeepestConnections },
                { ConnectionsVisibility.All, ShowAllConnections },
                { ConnectionsVisibility.Project, ShowDeepestConnections },
                { ConnectionsVisibility.Custom, ShowCustomConnections },
            };
        }

        private void ShowDeepestConnections()
        {
            foreach (var nc in nodes.Values)
            {
                var rn = nc.PRT.GetDeepestReference(); // referenced node;
                if (rn) Connect(nodes[rn].node, nc.node);
            }
        }

        private void ShowAllConnections()
        {
            foreach (var nc in nodes.Values)
                foreach (var r in nc.PRT.References)
                    Connect(nc.node, nodes[r].node);
        }

        private void ShowCustomConnections()
        {
            ShowDeepestConnections();
            //foreach (var nc in nodes.Values) {
            //	nc.
            //}
        }


        private void ClearConnections(NodeViewModel nodeViewModel)
        {
            foreach (var i in nodeViewModel.Inputs)
            {
                while (i.Connections.Count > 0)
                {
                    _networkViewModel.Connections.Remove(i.Connections[0]);
                }
            }
        }

        private void Connect(NodeViewModel tn, NodeViewModel n)
        {
            var c = new ConnectionViewModel(_networkViewModel,
                    n.Inputs[0], tn.Outputs[0]);
            _networkViewModel.Connections.Add(c);
        }
        #endregion

        #region Highlighting
        public void ResetHighlights()
        {
            foreach (var n in nodes.Values)
            {
                if (n.PRT.DepthLevel == 0) n.node.BackColor = Colors.DarkOliveGreen;
                else n.node.BackColor = Colors.RoyalBlue;
            }
        }

        public void HighlightReferencingNodes(ProjectReferenceTree r)
        {
            ResetHighlights();
            r.WithEach(i =>
            {
                if (i.References.Contains(r))
                    nodes[i].node.BackColor = Colors.LightBlue;
            });
        }

        public void HighlightReferencedNodes(ProjectReferenceTree r)
        {
            ResetHighlights();
            foreach (var sr in r.References)
                nodes[sr].node.BackColor = Colors.Orange;
        }
        #endregion

        public void RequestShow(ProjectReferenceTree r)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            SHOW_PROJECT_REQUEST?.Invoke(r.Item.Name);
        }

        /// <summary>Returns new context only when node was not already created, othwerwise null.</summary>
        /// <param name="r"></param>
        /// <returns></returns>
        public RefContext CreateRefContext(ProjectReferenceTree r)
        {
            if (nodes.ContainsKey(r)) return null;
            return GetRefContext(r);
        }

        private RefContext GetRefContext(ProjectReferenceTree r)
        {
            if (nodes.ContainsKey(r)) return nodes[r];
            var nc = new RefContext()
            {
                node = CreateNode(r),
                PRT = r,
            };
            nc.node.context = nc;
            r.data = nc;
            nodes.Add(r, nc);
            return nc;
        }

        private CustomNodeViewModel CreateNode(ProjectReferenceTree r)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var n = new CustomNodeViewModel();
            n.nodes = this;
            n.Name = r.Item.Name;
            //n.BackColor = Colors.DarkOliveGreen;
            //n.IsCollapsed = true;
            _networkViewModel.Nodes.Add(n);

            var inp = new NodeInputViewModel();
            inp.Name = "Referenced by";
            inp.PortPosition = PortPosition.Left;
            inp.MaxConnections = int.MaxValue;
            n.Inputs.Add(inp);

            var ou = new NodeOutputViewModel();
            ou.Name = "References";
            ou.PortPosition = PortPosition.Right;
            ou.MaxConnections = int.MaxValue;
            n.Outputs.Add(ou);
            return n;
        }

        public static implicit operator bool(ProjectsNodesView n) => n != null;
    }

    public enum ConnectionsVisibility
    {
        Deepest,    // Deepest reference tree
        All,        // All References Tree
        Project,    // Only Project Dependencies
        Custom
    }

    public class RefContext
    {
        public CustomNodeViewModel node;
        public ProjectReferenceTree PRT;

        public override string ToString()
        {
            return PRT?.ToString() ?? base.ToString();
        }

        public static implicit operator bool(RefContext c) => c != null;
    }
}
