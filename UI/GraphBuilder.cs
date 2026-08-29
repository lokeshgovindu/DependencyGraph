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
        public static Graph Build(ProjectReferenceTree prt, ConnectionsVisibility visibility,
            LayerDirection direction, GraphPalette palette, int maxDepth = UnlimitedDepth,
            Func<string, string> folderOf = null, ICollection<string> focusKeys = null)
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

            foreach (var project in included)
            {
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

                    subgraphs[folder] = subgraph;
                    graph.RootSubgraph.AddSubgraph(subgraph);
                }

                subgraph.AddNode(node);
            }

            AddEdges(graph, included, visibility, palette);
            return graph;
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
            ConnectionsVisibility visibility, GraphPalette palette)
        {
            // A reference is only a cycle if it points back at something already on the path from
            // the root, so work that out once over the whole set rather than per edge.
            var backEdges = FindBackEdges(included);

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
                        Style(graph.AddEdge(project.Key, reference.Key), palette,
                            backEdges.Contains((project, reference)));
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

                Style(graph.AddEdge(referencedBy.Key, project.Key), palette,
                    backEdges.Contains((referencedBy, project)));
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
