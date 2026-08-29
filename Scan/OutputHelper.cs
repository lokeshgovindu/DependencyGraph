using DependencyGraph.Scan;
using EnvDTE80;
using Microsoft.VisualStudio.GraphModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Project = EnvDTE.Project;
using Microsoft.VisualStudio.Shell;
using System.IO;
using System.ComponentModel;
using System.Xml.Linq;

namespace DependencyGraph.Scan
{
    internal class OutputHelper
    {
        public static bool CreateProjectDGML(ProjectReferenceTree prt, string filePath)
        {
            Dictionary<string, GraphNode> name2nodesMap = new Dictionary<string, GraphNode>();

            // Create a new Graph object
            Graph graph = new Graph();

            // Keyed by the project's stable key, not its name: two projects in different
            // solution folders can share a name, and keying on the name merged them into one
            // node. The name is only the label.
            foreach (var refPRT in prt.AllPRTs)
            {
                name2nodesMap[refPRT.Key] = graph.Nodes.GetOrCreate(refPRT.Key);
                name2nodesMap[refPRT.Key].Label = refPRT.ProjectName;
            }

            // Create a link between the nodes
            foreach (var refPRT in prt.AllPRTs)
            {
                foreach (var refProj in refPRT.References)
                {
                    var link = graph.Links.GetOrCreate(name2nodesMap[refPRT.Key], name2nodesMap[refProj.Key]);
                }
            }

            // Save the graph to a DGML file
            graph.Save(filePath);

            return true;
        }

        public static bool CreateGraphvizDot(ProjectReferenceTree prt, string filePath)
        {
            using (StreamWriter writer = new StreamWriter(filePath))
            {
                // Create GraphViz's dot digraph
                writer.WriteLine("digraph {");

                // Declare every node by its stable key with the project name as the label, so
                // two projects that share a name stay separate and isolated projects still
                // appear at all.
                foreach (var item in prt.AllPRTs)
                    writer.WriteLine("    {0} [label={1}]",
                        DotId(item.Key), DotId(item.ProjectName));

                foreach (var item in prt.AllPRTs)
                    foreach (var subItem in item.References)
                        writer.WriteLine("    {0} -> {1}", DotId(item.Key), DotId(subItem.Key));

                writer.WriteLine("}");
            }
            return true;
        }

        /// <summary>
        /// Quotes a project name so it survives as a DOT identifier. Unquoted, anything with a
        /// space breaks the file outright and a dot is read as a port separator, which silently
        /// mangles the very common Company.Module.Core style of project name.
        /// </summary>
        private static string DotId(string projectName)
        {
            return "\"" + projectName.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        internal static bool CreateGraphvizSVG(ProjectReferenceTree prt, string filePath)
        {
            return CreateGraphvizOutput(prt, "svg", filePath);
        }

        internal static bool CreateGraphvizPNG(ProjectReferenceTree prt, string filePath)
        {
            return CreateGraphvizOutput(prt, "png", filePath);
        }

        internal static bool CreateGraphvizOutput(ProjectReferenceTree prt, string outputFormat, string outputFilePath)
        {
            string dotFilePath = outputFilePath + ".dot";
            CreateGraphvizDot(prt, dotFilePath);
            return RenderAndCleanUp(dotFilePath, outputFormat, outputFilePath);
        }

        internal static bool CreateGraphvizOutput(string inputDotFilePath, string outputFormat, string outputFilePath)
        {
            try
            {
                // Quote the paths - the default save location sits under the user's profile,
                // so an unquoted path with a space in it is the normal case, not the exception.
                string args = String.Format("-T{0} -o\"{1}\" \"{2}\"",
                    outputFormat, outputFilePath, inputDotFilePath);
                using (var p = new System.Diagnostics.Process())
                {
                    p.StartInfo.UseShellExecute = false;
                    p.StartInfo.CreateNoWindow = true;
                    p.StartInfo.FileName = FindGraphvizDot() ?? "dot";
                    p.StartInfo.Arguments = args;
                    p.Start();
                    p.WaitForExit();
                    return p.ExitCode == 0;
                }
            }
            catch (Win32Exception)
            {
                throw new Exception("'dot' command is not on path");
            }
        }

        /// <summary>
        /// Renders a dot file, then removes it on success - it is only an intermediate on the way
        /// to the SVG/PNG, and leaving it behind litters the output folder with "graph.png.dot".
        /// It is deliberately kept when rendering fails, so the input can be inspected.
        /// </summary>
        private static bool RenderAndCleanUp(string dotFilePath, string outputFormat, string outputFilePath)
        {
            bool rendered = CreateGraphvizOutput(dotFilePath, outputFormat, outputFilePath);
            if (rendered)
            {
                try { System.IO.File.Delete(dotFilePath); } catch { /* not worth failing over */ }
            }
            return rendered;
        }

        private static string GetTempFilePath(string ext)
        {
            return System.IO.Path.GetTempPath() + Guid.NewGuid().ToString() + "." + ext;
        }

        /// <summary>
        /// Graphviz's dot.exe, from the setting if one is configured, otherwise from the PATH.
        /// Null when it cannot be found.
        /// </summary>
        /// <remarks>
        /// The setting exists because getting dot.exe onto the PATH is the step people get wrong:
        /// a reviewer reported installing Graphviz, verifying the PATH, and still seeing nothing.
        /// Pointing straight at the executable removes that failure mode.
        /// </remarks>
        public static string FindGraphvizDot()
        {
            string configured = null;
            try { configured = State.Settings.Instance.GraphvizDotPath; } catch { }

            if (!string.IsNullOrWhiteSpace(configured))
                return System.IO.File.Exists(configured) ? configured : null;

            return FindOnPath("dot.exe");
        }

        /// <summary>True when Graphviz's 'dot' can be found, so the SVG/PNG options are usable.</summary>
        public static bool IsGraphvizAvailable()
        {
            return FindGraphvizDot() != null;
        }

        public static string FindOnPath(string fileName)
        {
            if (System.IO.File.Exists(fileName)) return System.IO.Path.GetFullPath(fileName);

            var paths = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(paths)) return null;

            foreach (var path in paths.Split(System.IO.Path.PathSeparator))
            {
                try
                {
                    var fullPath = System.IO.Path.Combine(path, fileName);
                    if (System.IO.File.Exists(fullPath)) return fullPath;
                }
                catch (ArgumentException)
                {
                    // A quoted or otherwise malformed PATH entry - skip it rather than fail.
                }
            }
            return null;
        }

        internal static bool CreateSolutionDGML(VSScanner vsScanner, string filePath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            List<EnvDTE.Project> AllProjects = vsScanner.Projects;
            Dictionary<string, GraphNode> name2nodesMap = new Dictionary<string, GraphNode>();

            // Create a new Graph object
            Graph graph = new Graph();

            // Keyed by stable key rather than name, so same-named projects in different solution
            // folders stay separate.
            foreach (var proj in AllProjects)
            {
                string key = ProjectReferenceTree.KeyOf(proj);
                name2nodesMap[key] = graph.Nodes.GetOrCreate(key);
                name2nodesMap[key].Label = proj.Name;
            }

            // Create a link between the nodes
            foreach (var item in AllProjects)
            {
                var from = name2nodesMap[ProjectReferenceTree.KeyOf(item)];
                foreach (var refProj in item.ProjectReferences())
                {
                    // A referenced project need not be one the scan returned - it can live
                    // outside the solution, or be of a kind we skip - so create it on demand
                    // instead of indexing into the map and throwing.
                    string refKey = ProjectReferenceTree.KeyOf(refProj);
                    if (!name2nodesMap.TryGetValue(refKey, out var to))
                    {
                        to = graph.Nodes.GetOrCreate(refKey);
                        to.Label = refProj.Name;
                        name2nodesMap[refKey] = to;
                    }

                    var link = graph.Links.GetOrCreate(from, to);
                }
            }

            // Save the graph to a DGML file
            graph.Save(filePath);

            return true;
        }

        internal static bool CreateSolutionGraphvizDot(VSScanner vsScanner, string filePath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            using (StreamWriter writer = new StreamWriter(filePath))
            {
                // Create GraphViz's dot digraph
                writer.WriteLine("digraph {");
                foreach (var item in vsScanner.Projects)
                {
                    var references = item.ProjectReferences();
                    if (references.Count == 0)
                        writer.WriteLine("    {0}", DotId(item.Name));

                    foreach (var subItem in references)
                    {
                        writer.WriteLine("    {0} -> {1}", DotId(item.Name), DotId(subItem.Name));
                    }
                }
                writer.WriteLine("}");
            }
            return true;
        }

        internal static bool CreateSolutionGraphvizSVG(VSScanner vsScanner, string outputFilePath)
        {
            string dotFilePath = outputFilePath + ".dot";
            CreateSolutionGraphvizDot(vsScanner, dotFilePath);
            return RenderAndCleanUp(dotFilePath, "svg", outputFilePath);
        }

        internal static bool CreateSolutionGraphvizPNG(VSScanner vsScanner, string outputFilePath)
        {
            string dotFilePath = outputFilePath + ".dot";
            CreateSolutionGraphvizDot(vsScanner, dotFilePath);
            return RenderAndCleanUp(dotFilePath, "png", outputFilePath);
        }
    }
}
