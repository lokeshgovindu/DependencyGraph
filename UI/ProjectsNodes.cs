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


        private ConnectionsVisibility _displayMethod = ConnectionsVisibility.Deepest;

        public ConnectionsVisibility DisplayMethod
        {
            get => _displayMethod;
            set
            {
                _displayMethod = value;
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
                _nodesCount = _nodes.Count;
            }
        }

        private NetworkViewModel    _networkViewModel;
        public NetworkViewModel     NVM => _networkViewModel;

        public ProjectsNodesView(double w = 800, double h = 600)
        {
            this.Size = (w, h);
            _networkViewModel = new NetworkViewModel();
            View = new NetworkView { ViewModel = _networkViewModel };
            SetDisplayMethods();
        }

        private readonly Dictionary<ProjectReferenceTree, RefContext> _nodes = new Dictionary<ProjectReferenceTree, RefContext>();
        private int _nodesCount         = 0;
        public  int  NodesCount         => _nodesCount;
        private int _connectionsCount   = 0;
        public  int  ConnectionsCount   => _connectionsCount;

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
                ln = rol.Max(sr => sr.ProjectName.Length);
                rol = prt.AllOnLevel(++cl);
            }
        }

        #region Connections

        public void ShowConnections()
        {
            _networkViewModel.Connections.Clear();
            displayMethods[DisplayMethod]();
            _connectionsCount = _networkViewModel.Connections.Count;
        }

        private Dictionary<ConnectionsVisibility, Action> displayMethods;
        private void SetDisplayMethods()
        {
            displayMethods = new Dictionary<ConnectionsVisibility, Action>() {
                { ConnectionsVisibility.Deepest, ShowDeepestConnections },
                { ConnectionsVisibility.All,     ShowAllConnections     },
                { ConnectionsVisibility.Project, ShowDeepestConnections },
                { ConnectionsVisibility.Custom,  ShowCustomConnections  },
            };
        }

        private void ShowDeepestConnections()
        {
            foreach (var nc in _nodes.Values)
            {
                var rn = nc.PRT.GetDeepestReference(); // referenced node;
                if (rn) Connect(_nodes[rn].node, nc.node);
            }
        }

        private void ShowAllConnections()
        {
            foreach (var nc in _nodes.Values)
                foreach (var r in nc.PRT.References)
                    Connect(nc.node, _nodes[r].node);
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
            foreach (var n in _nodes.Values)
            {
                if (n.PRT.DepthLevel == 0) n.node.BackColor = Colors.DarkOliveGreen;
                else n.node.BackColor = Colors.RoyalBlue;
            }
        }

        public void HighlightReferencingNodes(ProjectReferenceTree prt)
        {
            ResetHighlights();
            prt.WithEach(i =>
            {
                if (i.References.Contains(prt))
                    _nodes[i].node.BackColor = Colors.LightBlue;
            });
        }

        public void HighlightReferencedNodes(ProjectReferenceTree prt)
        {
            ResetHighlights();
            foreach (var sr in prt.References)
                _nodes[sr].node.BackColor = Colors.Orange;
        }
        #endregion

        public void RequestShow(ProjectReferenceTree prt)
        {
            SHOW_PROJECT_REQUEST?.Invoke(prt.ProjectName);
        }

        /// <summary>Returns new context only when node was not already created, othwerwise null.</summary>
        /// <param name="r"></param>
        /// <returns></returns>
        public RefContext CreateRefContext(ProjectReferenceTree prt)
        {
            if (_nodes.ContainsKey(prt)) return null;
            return GetRefContext(prt);
        }

        private RefContext GetRefContext(ProjectReferenceTree prt)
        {
            if (_nodes.ContainsKey(prt)) return _nodes[prt];
            var nc = new RefContext()
            {
                node = CreateNode(prt),
                PRT = prt,
            };
            nc.node.context = nc;
            prt.data = nc;
            _nodes.Add(prt, nc);
            return nc;
        }

        private CustomNodeViewModel CreateNode(ProjectReferenceTree prt)
        {
            var viewModel   = new CustomNodeViewModel();
            viewModel.nodes = this;
            viewModel.Name  = prt.ProjectName;
            //n.BackColor = Colors.DarkOliveGreen;
            //n.IsCollapsed = true;
            _networkViewModel.Nodes.Add(viewModel);

            var inViewModel             = new NodeInputViewModel();
            inViewModel.Name            = "ReferencedBy: " + prt.ReferencedByCount;
            inViewModel.PortPosition    = PortPosition.Left;
            inViewModel.MaxConnections  = int.MaxValue;
            viewModel.Inputs.Add(inViewModel);

            var outViewModel            = new NodeOutputViewModel();
            outViewModel.Name           = "References: " + prt.ReferencesCount;
            outViewModel.PortPosition   = PortPosition.Right;
            outViewModel.MaxConnections = int.MaxValue;
            viewModel.Outputs.Add(outViewModel);
            return viewModel;
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
