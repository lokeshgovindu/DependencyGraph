using System.Collections.Generic;
using DependencyGraph.Scan;
using Microsoft.Msagl.Drawing;

namespace DependencyGraph.UI
{
    /// <summary>Which references are drawn as edges.</summary>
    public enum ConnectionsVisibility
    {
        Deepest,    // one edge per project, from the deepest project that references it
        All,        // every reference of every project
        Project     // the selected project and its direct references only
    }

    /// <summary>
    /// Builds the MSAGL graph for a project reference tree. Layout itself - layer assignment,
    /// ordering within a layer, edge routing - is MSAGL's job; all this does is describe the
    /// nodes, the edges and how they should look.
    /// </summary>
    internal static class GraphBuilder
    {
        /// <summary>
        /// Point size for node labels. MSAGL sizes each node around its label, so raising this
        /// enlarges the whole graph proportionally - which is the point, since the view opens at
        /// 100% and the labels have to be readable at 1:1.
        /// </summary>
        public const double LabelFontSize = 14;

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

        public static Graph Build(ProjectReferenceTree prt, ConnectionsVisibility visibility,
            LayerDirection direction, GraphPalette palette)
        {
            var graph = new Graph("dependencies");

            graph.Attr.LayerDirection    = direction;
            graph.Attr.BackgroundColor   = palette.Background;
            graph.Attr.NodeSeparation    = 25;
            graph.Attr.LayerSeparation   = 45;
            graph.Attr.OptimizeLabelPositions = true;

            foreach (var project in prt.AllPRTs)
            {
                var node = graph.AddNode(project.ProjectName);
                node.UserData = project;
                node.LabelText = project.ProjectName;

                node.Attr.Shape     = Shape.Box;
                node.Attr.XRadius   = 3;
                node.Attr.YRadius   = 3;
                node.Attr.LineWidth   = 1;
                node.Attr.Padding     = 3;
                node.Attr.LabelMargin = LabelMargin;

                bool isRoot = project.DepthLevel == 0;
                node.Attr.FillColor  = isRoot ? palette.RootFill : palette.NodeFill;
                node.Attr.Color      = isRoot ? palette.RootStroke : palette.NodeStroke;
                node.Label.FontColor = isRoot ? palette.RootText : palette.NodeText;
                node.Label.FontName  = LabelFontName;
                node.Label.FontSize  = LabelFontSize;
            }

            AddEdges(graph, prt, visibility, palette);
            return graph;
        }

        private static void AddEdges(Graph graph, ProjectReferenceTree prt,
            ConnectionsVisibility visibility, GraphPalette palette)
        {
            if (visibility == ConnectionsVisibility.All)
            {
                foreach (var project in prt.AllPRTs)
                    foreach (var reference in project.References)
                        Style(graph.AddEdge(project.ProjectName, reference.ProjectName), palette);

                return;
            }

            // Deepest, and "project references only" - which is already a two-level tree, so the
            // deepest reference of each of its nodes is the root.
            foreach (var project in prt.AllPRTs)
            {
                var referencedBy = project.GetDeepestReference();
                if (referencedBy == null || referencedBy == project) continue;
                Style(graph.AddEdge(referencedBy.ProjectName, project.ProjectName), palette);
            }
        }

        private static void Style(Edge edge, GraphPalette palette)
        {
            edge.Attr.Color = palette.Edge;
            edge.Attr.LineWidth = 1;
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
