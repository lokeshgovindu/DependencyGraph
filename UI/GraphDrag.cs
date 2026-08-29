using System.Collections.Generic;
using Microsoft.Msagl.Core.Geometry;
using Microsoft.Msagl.Core.Layout;
using Microsoft.Msagl.Drawing;
using DrawingEdge = Microsoft.Msagl.Drawing.Edge;
using GeometryNode = Microsoft.Msagl.Core.Layout.Node;

namespace DependencyGraph.UI
{
    /// <summary>
    /// Moving a node, or a whole solution-folder box, after the graph has been laid out.
    /// </summary>
    /// <remarks>
    /// MSAGL ships a layout editor for this (GraphViewer.LayoutEditingEnabled), and it must not be
    /// used: with it on, the first drag ended the whole devenv process - no exception dialog,
    /// nothing in the activity log, nothing in Windows Error Reporting - reproduced with and
    /// without grouping. What a drag actually needs is small enough to do here, and being separate
    /// from the control means it can be checked offscreen.
    /// </remarks>
    internal static class GraphDrag
    {
        /// <summary>What a drag moved, for the caller to repaint.</summary>
        /// <remarks>
        /// Moving the geometry is only half of it: an element whose geometry moved but which was
        /// never invalidated keeps drawing where it was. That is what dragging a folder box looked
        /// like at first - the box moved and the projects inside it stayed behind, because only
        /// the box itself was being invalidated.
        /// </remarks>
        public sealed class Moved
        {
            public readonly List<Microsoft.Msagl.Drawing.Node> Nodes =
                new List<Microsoft.Msagl.Drawing.Node>();

            public readonly List<DrawingEdge> Edges = new List<DrawingEdge>();
        }

        /// <summary>
        /// Shifts <paramref name="node"/> by <paramref name="delta"/>, moving every project inside
        /// it if it is a folder box, and redraws the references that touch what moved.
        /// </summary>
        public static Moved Move(Graph graph, Microsoft.Msagl.Drawing.Node node, Point delta)
        {
            var result = new Moved();

            var geometry = node?.GeometryNode;
            if (graph == null || geometry == null) return result;

            var moved = new HashSet<GeometryNode>();

            if (geometry is Cluster cluster)
            {
                foreach (var member in Members(cluster))
                {
                    member.Center += delta;
                    moved.Add(member);
                }

                // The box is moved rather than recomputed from its children. Recomputing
                // (CalculateBoundsFromChildren) re-fits it tightly and throws away the margins
                // MSAGL worked out during layout - which includes the band across the top that
                // the folder's name is drawn in, so the box shrank and the name fell outside it.
                foreach (var inner in cluster.AllClustersDepthFirst())
                {
                    inner.BoundaryCurve?.Translate(delta);
                    inner.RectangularBoundary?.TranslateRectangle(delta);
                }
            }
            else
            {
                geometry.Center += delta;
                moved.Add(geometry);
            }

            foreach (var drawingNode in graph.Nodes)
                if (drawingNode.GeometryNode != null && moved.Contains(drawingNode.GeometryNode))
                    result.Nodes.Add(drawingNode);

            // A folder box is not among the graph's nodes - it lives in the subgraph tree - so it
            // has to be added by hand. A plain node the loop above already found.
            if (!result.Nodes.Contains(node)) result.Nodes.Add(node);

            foreach (var edge in graph.Edges)
            {
                var edgeGeometry = edge.GeometryEdge;
                if (edgeGeometry == null) continue;
                if (!moved.Contains(edgeGeometry.Source) && !moved.Contains(edgeGeometry.Target))
                    continue;

                // Straight lines rather than a re-route: re-running MSAGL's router would lay the
                // whole graph out again and undo the drag. Only the references that moved are
                // touched, so everything else keeps the routing it was given.
                Microsoft.Msagl.Routing.StraightLineEdges.RouteEdge(edgeGeometry, 0);
                result.Edges.Add(edge);
            }

            // The graph's own bounds do not follow its nodes on their own, and everything that
            // frames or exports the graph is measured from them - so without this, dragging a node
            // outside the original extent left it off the edge of a saved PNG, and the 100% view
            // centred on the wrong middle.
            graph.GeometryGraph?.UpdateBoundingBox();

            return result;
        }

        /// <summary>Every project in a folder box, including the ones in nested boxes.</summary>
        private static IEnumerable<GeometryNode> Members(Cluster cluster)
        {
            foreach (var inner in cluster.AllClustersDepthFirst())
                foreach (var node in inner.Nodes)
                    yield return node;
        }
    }
}
