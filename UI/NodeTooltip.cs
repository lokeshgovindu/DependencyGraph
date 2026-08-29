using System;
using System.Text;
using MsaglNode = Microsoft.Msagl.Drawing.Node;

namespace DependencyGraph.UI
{
    /// <summary>
    /// What to say about a node when the pointer rests on it.
    /// </summary>
    /// <remarks>
    /// A box in the graph has room for a project's short name and nothing else, so everything else
    /// worth knowing about it - where it is, how many projects it references, how many reference
    /// it, which solution folder it is in, whether it is even part of this solution - has to be
    /// somewhere. The status bar shows it for the one selected project; this shows it for whatever
    /// the pointer is over, without changing the selection.
    ///
    /// Separate from the control so it can be checked offscreen: the control itself cannot be
    /// constructed without the Visual Studio shell.
    /// </remarks>
    internal static class NodeTooltip
    {
        /// <summary>
        /// The tooltip for a node, or null for empty canvas - which is the signal not to show one.
        /// </summary>
        /// <param name="folderOf">
        /// Optional lookup from project key to solution folder; null when the solution has not
        /// been scanned, or when the folders are not wanted.
        /// </param>
        public static string For(MsaglNode node, Func<string, string> folderOf)
        {
            if (node == null) return null;

            string collapsed = GraphBuilder.CollapsedFolderOf(node);
            if (collapsed != null)
            {
                return collapsed
                     + "\n\nA collapsed solution folder. The projects in it are drawn as this one"
                     + "\nnode, and every reference into or out of the folder points at it."
                     + "\n\nClick it to expand the folder again.";
            }

            var project = GraphBuilder.ProjectOf(node);
            if (project == null) return node.LabelText;

            var text = new StringBuilder();
            text.Append(project.ProjectName);

            string folder = folderOf == null ? null : folderOf(project.Key);
            if (!string.IsNullOrEmpty(folder))
                text.Append("\nSolution folder: ").Append(folder);

            text.Append("\n\n");

            if (project.IsExternal)
            {
                text.Append("Referenced from this solution but not part of it, so its own");
                text.Append("\nreferences were never scanned - the graph below it is incomplete.");
                text.Append("\n\n");
            }

            text.Append("References ").Append(project.ReferencesCount)
                .Append(project.ReferencesCount == 1 ? " project" : " projects");
            text.Append("\nReferenced by ").Append(project.ReferencedByCount)
                .Append(project.ReferencedByCount == 1 ? " project" : " projects");

            if (project.DepthLevel == 0)
                text.Append("\n\nThe project this graph is drawn from.");

            // The key is the solution-relative path, which is what tells two projects with the
            // same name apart - and the graph cannot show it.
            if (project.Key != project.ProjectName)
                text.Append("\n\n").Append(project.Key);

            text.Append("\n\nDouble-click to draw the graph from this project.");
            return text.ToString();
        }
    }
}
