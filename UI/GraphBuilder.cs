using System;
using System.Linq;
using System.Collections.Generic;
using DependencyGraph.Scan;
using Microsoft.Msagl.Drawing;

namespace DependencyGraph.UI
{
    /// <summary>Which references are drawn as edges.</summary>
    public enum ConnectionsVisibility
    {
        Deepest,      // one edge per project, from the deepest project that references it
        All,          // every reference of every project
        Project,      // the selected project and its direct references only
        ReferencedBy  // everything that depends on the project, directly or transitively
    }

    /// <summary>
    /// Builds the MSAGL graph for a project reference tree. Layout itself - layer assignment,
    /// ordering within a layer, edge routing - is MSAGL's job; all this does is describe the
    /// nodes, the edges and how they should look.
    /// </summary>
    internal static class GraphBuilder
    {
        /// <summary>
        /// Point size for node labels, from the options page. MSAGL sizes each node around its
        /// label, so this scales the whole graph - which is the point, since the view opens at
        /// 100% and the labels have to be readable at 1:1.
        /// </summary>
        public static double LabelFontSize
        {
            get
            {
                try { return Clamp(State.Settings.Instance.LabelFontSize, 6, 32); }
                catch { return 14; }
            }
        }

        private static double Clamp(double value, double min, double max)
        {
            return value < min ? min : (value > max ? max : value);
        }

        /// <summary>
        /// Font for node labels.
        /// </summary>
        /// <remarks>
        /// Must be a font that actually exists. MSAGL defaults Label.FontName to "Times-Roman",
        /// which Windows does not have: it measures the node box with WPF's fallback for that
        /// name and then renders with another, so the text spills out of its box. Naming a real
        /// font makes the measure and the render agree.
        /// </remarks>
        public const string LabelFontName = "Segoe UI";

        /// <summary>Space between the label and the node border, in points.</summary>
        private const int LabelMargin = 6;

        /// <summary>Value of <c>maxDepth</c> meaning "no limit".</summary>
        public const int UnlimitedDepth = int.MaxValue;

        /// <param name="folderOf">
        /// Optional lookup from project key to solution folder. When supplied, nodes are grouped
        /// into a cluster per folder.
        /// </param>
        /// <param name="focusKeys">
        /// When non-empty, only these projects are drawn, with the references between them.
        /// </param>
        /// <param name="collapsedFolders">
        /// Solution folders drawn as a single node standing in for everything inside them, with
        /// the references of their projects aggregated onto it.
        /// </param>
        public static Graph Build(ProjectReferenceTree prt, ConnectionsVisibility visibility,
            LayerDirection direction, GraphPalette palette, int maxDepth = UnlimitedDepth,
            Func<string, string> folderOf = null, ICollection<string> focusKeys = null,
            ICollection<string> collapsedFolders = null)
        {
            var graph = new Graph("dependencies");

            graph.Attr.LayerDirection    = direction;
            graph.Attr.BackgroundColor   = palette.Background;
            graph.Attr.NodeSeparation    = 25;
            graph.Attr.LayerSeparation   = 45;
            graph.Attr.OptimizeLabelPositions = true;

            // A deep solution graph is unreadable in one view, so only the requested number of
            // levels is drawn. The impact view is walked backwards, since its root is a sink.
            var included = WithinDepth(prt, maxDepth,
                backwards: visibility == ConnectionsVisibility.ReferencedBy);

            ApplyExclusions(included, prt);

            // Focus narrows the graph to a hand-picked set and the references between them,
            // which is the way to get a readable picture out of a busy solution.
            if (focusKeys != null && focusKeys.Count > 0)
                included.RemoveWhere(p => !focusKeys.Contains(p.Key));

            // One cluster per solution folder, created on demand. MSAGL lays clusters out as
            // boxes around their members, which is what makes the grouping visible.
            var subgraphs = new Dictionary<string, Subgraph>();
            if (folderOf != null && graph.RootSubgraph == null)
                graph.RootSubgraph = new Subgraph("root");

            // A collapsed folder is not drawn as a box with its projects inside it; the whole
            // folder becomes one node, and every reference into or out of the folder is redrawn
            // against that node. Which project inside it held the reference stops mattering, so
            // the counts here are only for the label.
            var collapsed = CollapsedFolders(included, folderOf, collapsedFolders);
            var idOf = NodeIdMap(included, folderOf, collapsed);

            foreach (var folder in collapsed)
            {
                var folderNode = graph.AddNode(FolderNodePrefix + folder.Key);
                folderNode.LabelText = folder.Key + "   (" + folder.Value
                    + (folder.Value == 1 ? " project)" : " projects)");

                folderNode.Attr.Shape     = Shape.Box;
                folderNode.Attr.XRadius   = 3;
                folderNode.Attr.YRadius   = 3;
                folderNode.Attr.LineWidth = 2;
                folderNode.Attr.Padding   = 3;
                folderNode.Attr.LabelMargin = LabelMargin;
                folderNode.Attr.FillColor  = palette.Background;
                folderNode.Attr.Color      = palette.NodeStroke;
                folderNode.Label.FontColor = palette.NodeText;
                folderNode.Label.FontName  = LabelFontName;
                folderNode.Label.FontSize  = LabelFontSize;
            }

            foreach (var project in included)
            {
                // A project inside a collapsed folder has no node of its own - the folder's node
                // stands in for it.
                if (folderOf != null && collapsed.ContainsKey(folderOf(project.Key) ?? "")) continue;

                // Node id is the project's stable key, not its name: two projects in different
                // solution folders can share a name, and using the name merged them into one
                // node. The name is only the label.
                var node = graph.AddNode(project.Key);
                node.UserData = project;
                node.LabelText = project.ProjectName;

                node.Attr.Shape     = Shape.Box;
                node.Attr.XRadius   = 3;
                node.Attr.YRadius   = 3;
                node.Attr.LineWidth   = 1;
                node.Attr.Padding     = 3;
                node.Attr.LabelMargin = LabelMargin;

                bool isRoot = project.DepthLevel == 0;

                // A project referenced from outside the solution is drawn dashed, so it is
                // obvious the graph below it is incomplete - its own references were never
                // scanned, because it is not in the solution to scan.
                if (!isRoot && project.IsExternal)
                {
                    node.Attr.AddStyle(Microsoft.Msagl.Drawing.Style.Dashed);
                    node.LabelText = project.ProjectName + "  (external)";
                }

                node.Attr.FillColor  = isRoot ? palette.RootFill : palette.NodeFill;
                node.Attr.Color      = isRoot ? palette.RootStroke : palette.NodeStroke;
                node.Label.FontColor = isRoot ? palette.RootText : palette.NodeText;
                node.Label.FontName  = LabelFontName;
                node.Label.FontSize  = LabelFontSize;

                if (folderOf == null) continue;

                string folder = folderOf(project.Key);
                if (string.IsNullOrEmpty(folder)) continue;

                if (!subgraphs.TryGetValue(folder, out var subgraph))
                {
                    subgraph = new Subgraph(folder) { LabelText = folder };
                    subgraph.Attr.Color = palette.NodeStroke;
                    subgraph.Attr.FillColor = palette.Background;
                    subgraph.Label.FontColor = palette.StatLabel;
                    subgraph.Label.FontName = LabelFontName;
                    subgraph.Label.FontSize = LabelFontSize;
                    MeasureLabel(subgraph.Label);

                    // The band across the top of the box, which the folder name is drawn in, is
                    // sized from the collapse button rather than from the label, so the button's
                    // diameter is what reserves room for the name. The button is worth keeping:
                    // the viewer wires it up, so clicking it folds the group down to a single box.
                    //
                    // Only the button's filled disc takes these colours - the chevron drawn on
                    // top of it keeps its own stroke - so the disc is left transparent until the
                    // pointer is over it, rather than sitting on the box corner as a grey blob.
                    subgraph.DiameterOfOpenCollapseButton = subgraph.Label.Height;
                    subgraph.CollapseButtonColorInactive  = Color.Transparent;
                    subgraph.CollapseButtonColorActive    = palette.StatValue;

                    subgraphs[folder] = subgraph;
                    graph.RootSubgraph.AddSubgraph(subgraph);
                }

                subgraph.AddNode(node);
            }

            AddEdges(graph, included, visibility, palette, idOf);
            return graph;
        }

        /// <summary>Node id prefix marking a node that stands in for a whole solution folder.</summary>
        private const string FolderNodePrefix = "folder:";

        /// <summary>
        /// The solution folder a node stands in for, or null for an ordinary project node.
        /// </summary>
        public static string CollapsedFolderOf(Node node)
        {
            return node != null && node.Id != null && node.Id.StartsWith(FolderNodePrefix,
                       StringComparison.Ordinal)
                ? node.Id.Substring(FolderNodePrefix.Length)
                : null;
        }

        /// <summary>Which of the collapsed folders are actually in this graph, and how big.</summary>
        private static Dictionary<string, int> CollapsedFolders(
            HashSet<ProjectReferenceTree> included, Func<string, string> folderOf,
            ICollection<string> collapsedFolders)
        {
            var counts = new Dictionary<string, int>();
            if (folderOf == null || collapsedFolders == null || collapsedFolders.Count == 0)
                return counts;

            foreach (var project in included)
            {
                string folder = folderOf(project.Key);
                if (string.IsNullOrEmpty(folder) || !collapsedFolders.Contains(folder)) continue;

                counts.TryGetValue(folder, out int n);
                counts[folder] = n + 1;
            }

            return counts;
        }

        /// <summary>
        /// Which node each project's edges attach to: its own, or its collapsed folder's.
        /// </summary>
        private static Dictionary<string, string> NodeIdMap(HashSet<ProjectReferenceTree> included,
            Func<string, string> folderOf, Dictionary<string, int> collapsed)
        {
            var map = new Dictionary<string, string>();
            foreach (var project in included)
            {
                string folder = folderOf == null ? null : folderOf(project.Key);
                map[project.Key] = folder != null && collapsed.ContainsKey(folder)
                    ? FolderNodePrefix + folder
                    : project.Key;
            }

            return map;
        }

        /// <summary>
        /// Gives every folder box a text element for its name.
        /// </summary>
        /// <remarks>
        /// Must be called before the graph is handed to the viewer. The viewer builds a text
        /// element for each ordinary node on its way to measuring that node's boundary, but a
        /// cluster's boundary comes from cluster layout instead, so a subgraph never gets one and
        /// its name is silently dropped. RegisterLabelCreator is the supported way to supply one.
        /// </remarks>
        public static FolderNames RegisterSubgraphLabels(Graph graph,
            Microsoft.Msagl.WpfGraphControl.GraphViewer viewer)
        {
            var names = new FolderNames();
            if (graph?.RootSubgraph == null || viewer == null) return names;

            foreach (var subgraph in graph.RootSubgraph.AllSubgraphsDepthFirstExcludingSelf())
            {
                var owner = subgraph;
                if (string.IsNullOrEmpty(owner.LabelText)) continue;

                viewer.RegisterLabelCreator(owner, _ =>
                {
                    var element = FolderNameElement(owner.Label);
                    names.Elements[owner] = element;
                    return element;
                });
                names.Owners.Add(owner);
            }

            return names;
        }

        private static System.Windows.Controls.TextBlock FolderNameElement(Label label)
        {
            var text = new System.Windows.Controls.TextBlock
            {
                Text       = label.Text,
                FontFamily = new System.Windows.Media.FontFamily(label.FontName),
                FontSize   = label.FontSize,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(
                        label.FontColor.R, label.FontColor.G, label.FontColor.B)),
            };

            // The viewer positions the element from its own size, which is only known once it has
            // been measured.
            text.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            text.Width  = text.DesiredSize.Width;
            text.Height = text.DesiredSize.Height;

            return text;
        }

        /// <summary>
        /// The folder-name elements on the current graph: what was registered with the viewer, and
        /// where each name has to sit.
        /// </summary>
        public sealed class FolderNames
        {
            internal readonly List<Subgraph> Owners = new List<Subgraph>();

            internal readonly Dictionary<Subgraph, System.Windows.Controls.TextBlock> Elements =
                new Dictionary<Subgraph, System.Windows.Controls.TextBlock>();

            /// <summary>Drops these registrations, for a rebuild that replaces them.</summary>
            public void Unregister(Microsoft.Msagl.WpfGraphControl.GraphViewer viewer)
            {
                if (viewer == null) return;
                foreach (var owner in Owners) viewer.UnregisterLabelCreator(owner);
                Owners.Clear();
                Elements.Clear();
            }

            /// <summary>
            /// Moves each folder name from the middle of its box up into the box's top band. Call
            /// after the graph has been laid out.
            /// </summary>
            /// <remarks>
            /// The viewer centres a node's label on the node, which for a box the size of a whole
            /// group drops the folder name across the middle of its own contents. Positioning is a
            /// RenderTransform rather than Canvas.Left/Top, and it carries MSAGL's y flip - hence
            /// the (1,0,0,-1) matrix, and why the y offset is measured to the label's bottom.
            /// </remarks>
            public void Reposition()
            {
                foreach (var pair in Elements)
                {
                    var cluster = pair.Key.GeometryNode as Microsoft.Msagl.Core.Layout.Cluster;
                    if (cluster == null) continue;

                    var element = pair.Value;
                    var box = cluster.BoundingBox;

                    double band = cluster.RectangularBoundary?.TopMargin ?? element.Height;
                    double centreY = box.Top - band / 2;

                    double x = box.Center.X - element.Width / 2;
                    double y = centreY + element.Height / 2;

                    // Called from LayoutUpdated, so it has to be quiet when nothing has moved -
                    // handing WPF a new transform on every layout pass would keep the canvas
                    // redrawing for no reason.
                    var current = element.RenderTransform?.Value ?? default(System.Windows.Media.Matrix);
                    if (Close(current.OffsetX, x) && Close(current.OffsetY, y) && current.M22 < 0)
                        continue;

                    element.RenderTransform = new System.Windows.Media.MatrixTransform(1, 0, 0, -1, x, y);
                }
            }

            private static bool Close(double a, double b)
            {
                return Math.Abs(a - b) < 0.01;
            }
        }

        /// <summary>
        /// Fills in a label's width and height by measuring its text.
        /// </summary>
        /// <remarks>
        /// Needed for cluster labels and only for those. MSAGL reserves the top margin a cluster
        /// draws its name in from the label's size, and the WPF viewer creates the text element
        /// only when there is a margin to put it in - but nothing in the pipeline measures a
        /// cluster label the way node labels get measured on the way to a node boundary. Left
        /// unmeasured the size stays 0x0, and the group box comes out as an unlabelled rectangle.
        /// </remarks>
        private static void MeasureLabel(Label label)
        {
            if (label == null || string.IsNullOrEmpty(label.Text)) return;

            var typeface = new System.Windows.Media.Typeface(label.FontName);
            var measured = new System.Windows.Media.FormattedText(
                label.Text,
                System.Globalization.CultureInfo.CurrentUICulture,
                System.Windows.FlowDirection.LeftToRight,
                typeface,
                label.FontSize,
                System.Windows.Media.Brushes.Black,
                1.0);

            label.Width  = measured.Width + 2 * LabelMargin;
            label.Height = measured.Height + LabelMargin;
        }

        /// <summary>
        /// The projects reachable from the root in at most <paramref name="maxDepth"/> references.
        /// </summary>
        /// <remarks>
        /// Measured by breadth-first search rather than by ProjectReferenceTree.DepthLevel.
        /// DepthLevel is the length of the *longest* path to a project - that is what the
        /// "deepest references only" view is built on - so filtering on it would hide a project
        /// the root references directly whenever a longer route to it also exists. On a solution
        /// where App references Core directly and also through Services, Core has DepthLevel 2
        /// or more, and "depth 1" would have dropped a direct reference.
        /// </remarks>
        private static HashSet<ProjectReferenceTree> WithinDepth(ProjectReferenceTree root,
            int maxDepth, bool backwards)
        {
            var within = new HashSet<ProjectReferenceTree> { root };
            if (maxDepth <= 0) return within;

            // For the impact view the arrows point towards the root, so the hops have to be
            // counted along the reverse edges.
            var neighbours = backwards ? Referencers(root.AllPRTs) : null;

            var queue = new Queue<KeyValuePair<ProjectReferenceTree, int>>();
            queue.Enqueue(new KeyValuePair<ProjectReferenceTree, int>(root, 0));

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (current.Value >= maxDepth) continue;

                IEnumerable<ProjectReferenceTree> next;
                if (backwards)
                    next = neighbours.TryGetValue(current.Key, out var list)
                        ? (IEnumerable<ProjectReferenceTree>)list
                        : new ProjectReferenceTree[0];
                else
                    next = current.Key.References;

                foreach (var neighbour in next)
                    if (within.Add(neighbour))
                        queue.Enqueue(new KeyValuePair<ProjectReferenceTree, int>(
                            neighbour, current.Value + 1));
            }

            return within;
        }

        /// <summary>
        /// Drops projects matching the "excluded projects" patterns from the set.
        /// </summary>
        /// <remarks>
        /// Aimed at the noise on a large solution - test and benchmark projects usually reference
        /// half of it and are rarely what you are looking at. The root is never excluded, since
        /// excluding the project you asked about would leave an empty window.
        /// </remarks>
        private static void ApplyExclusions(HashSet<ProjectReferenceTree> included,
            ProjectReferenceTree root)
        {
            string patterns;
            try { patterns = State.Settings.Instance.ExcludedProjects; }
            catch { return; }

            if (string.IsNullOrWhiteSpace(patterns)) return;

            var globs = patterns.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(p => p.Trim())
                                .Where(p => p.Length > 0)
                                .ToList();
            if (globs.Count == 0) return;

            included.RemoveWhere(p => p != root && globs.Any(g => Matches(p.ProjectName, g)));
        }

        /// <summary>Case-insensitive match supporting <c>*</c> and <c>?</c>.</summary>
        private static bool Matches(string name, string pattern)
        {
            string regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
                                     .Replace("\\*", ".*")
                                     .Replace("\\?", ".") + "$";
            return System.Text.RegularExpressions.Regex.IsMatch(name, regex,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        /// <summary>Which projects reference each project, inverted from the reference edges.</summary>
        private static Dictionary<ProjectReferenceTree, List<ProjectReferenceTree>> Referencers(
            HashSet<ProjectReferenceTree> all)
        {
            var map = new Dictionary<ProjectReferenceTree, List<ProjectReferenceTree>>();
            if (all == null) return map;

            foreach (var project in all)
                foreach (var reference in project.References)
                {
                    if (!map.TryGetValue(reference, out var list))
                        map[reference] = list = new List<ProjectReferenceTree>();
                    list.Add(project);
                }

            return map;
        }

        private static void AddEdges(Graph graph, HashSet<ProjectReferenceTree> included,
            ConnectionsVisibility visibility, GraphPalette palette,
            Dictionary<string, string> idOf)
        {
            // A reference is only a cycle if it points back at something already on the path from
            // the root, so work that out once over the whole set rather than per edge.
            var backEdges = FindBackEdges(included);

            // With a folder collapsed, several references can land on the same pair of nodes, and
            // references between two projects inside it become one node pointing at itself.
            var drawn = new HashSet<(string, string)>();

            void Add(ProjectReferenceTree from, ProjectReferenceTree to)
            {
                string fromId = idOf.TryGetValue(from.Key, out var f) ? f : from.Key;
                string toId   = idOf.TryGetValue(to.Key,   out var t) ? t : to.Key;

                if (fromId == toId) return;
                if (!drawn.Add((fromId, toId))) return;

                Style(graph.AddEdge(fromId, toId), palette, backEdges.Contains((from, to)));
            }

            // The impact view is a set of projects with the real edges between them, so every
            // edge within the set is drawn - same as "all references".
            if (visibility == ConnectionsVisibility.All
                || visibility == ConnectionsVisibility.ReferencedBy)
            {
                foreach (var project in included)
                    foreach (var reference in project.References)
                    {
                        // Skip edges into levels that were cut by the depth limit.
                        if (!included.Contains(reference)) continue;
                        Add(project, reference);
                    }

                return;
            }

            // Deepest, and "project references only" - which is already a two-level tree, so the
            // deepest reference of each of its nodes is the root.
            foreach (var project in included)
            {
                var referencedBy = project.GetDeepestReference();
                if (referencedBy == null || referencedBy == project) continue;
                if (!included.Contains(referencedBy)) continue;

                Add(referencedBy, project);
            }
        }

        /// <summary>
        /// References that close a cycle, found by depth-first search.
        /// </summary>
        /// <remarks>
        /// Visual Studio will not let you add a circular project reference through the UI, but a
        /// hand-edited project file can still produce one and the solution will load: it only
        /// fails at build time, with an error that does not say which projects are involved.
        /// Drawing those references in the error colour points straight at them.
        /// </remarks>
        private static HashSet<(ProjectReferenceTree, ProjectReferenceTree)> FindBackEdges(
            HashSet<ProjectReferenceTree> included)
        {
            var backEdges = new HashSet<(ProjectReferenceTree, ProjectReferenceTree)>();
            var visited = new HashSet<ProjectReferenceTree>();
            var onPath = new HashSet<ProjectReferenceTree>();

            foreach (var start in included)
                if (!visited.Contains(start))
                    Walk(start, included, visited, onPath, backEdges);

            return backEdges;
        }

        private static void Walk(ProjectReferenceTree node,
            HashSet<ProjectReferenceTree> included,
            HashSet<ProjectReferenceTree> visited,
            HashSet<ProjectReferenceTree> onPath,
            HashSet<(ProjectReferenceTree, ProjectReferenceTree)> backEdges)
        {
            visited.Add(node);
            onPath.Add(node);

            foreach (var reference in node.References)
            {
                if (!included.Contains(reference)) continue;

                if (onPath.Contains(reference)) backEdges.Add((node, reference));
                else if (!visited.Contains(reference))
                    Walk(reference, included, visited, onPath, backEdges);
            }

            onPath.Remove(node);
        }

        private static void Style(Edge edge, GraphPalette palette, bool isBackEdge)
        {
            edge.Attr.Color = isBackEdge ? palette.CycleEdge : palette.Edge;
            edge.Attr.LineWidth = isBackEdge ? 2 : 1;
            if (isBackEdge) edge.LabelText = "cycle";
        }

        /// <summary>Reads the project a node was built from, or null for a node we did not build.</summary>
        public static ProjectReferenceTree ProjectOf(Node node)
        {
            return node?.UserData as ProjectReferenceTree;
        }

        /// <summary>
        /// The rectangle the whole graph occupies on the viewer's canvas, for exporting an image
        /// of the entire graph rather than the part scrolled into view.
        /// </summary>
        /// <remarks>
        /// Taken from the graph's own geometry mapped through the viewer's pan/zoom transform,
        /// which is the only authoritative answer. Measuring the canvas's visual children instead
        /// does not work: MSAGL parks a viewport-sized background rectangle among them, and
        /// whether it is counter-transformed depends on when its layout handler last ran.
        /// </remarks>
        public static System.Windows.Rect ExportBounds(Graph graph,
            Microsoft.Msagl.WpfGraphControl.GraphViewer viewer)
        {
            if (graph?.GeometryGraph == null || viewer?.Transform == null)
                return System.Windows.Rect.Empty;

            var box = graph.BoundingBox;
            var transform = viewer.Transform;

            // All four corners, so the result stays correct even if the transform ever flips or
            // rotates an axis - MSAGL's y axis already points the other way.
            var corners = new[]
            {
                transform * new Microsoft.Msagl.Core.Geometry.Point(box.Left,  box.Bottom),
                transform * new Microsoft.Msagl.Core.Geometry.Point(box.Left,  box.Top),
                transform * new Microsoft.Msagl.Core.Geometry.Point(box.Right, box.Bottom),
                transform * new Microsoft.Msagl.Core.Geometry.Point(box.Right, box.Top),
            };

            double minX = corners[0].X, maxX = corners[0].X;
            double minY = corners[0].Y, maxY = corners[0].Y;
            foreach (var c in corners)
            {
                if (c.X < minX) minX = c.X;
                if (c.X > maxX) maxX = c.X;
                if (c.Y < minY) minY = c.Y;
                if (c.Y > maxY) maxY = c.Y;
            }

            return new System.Windows.Rect(minX, minY, maxX - minX, maxY - minY);
        }

        public static IEnumerable<Node> NodesOf(Graph graph)
        {
            return graph == null ? new Node[0] : graph.Nodes;
        }
    }
}
