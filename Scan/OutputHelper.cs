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

            foreach (var refPRT in prt.AllPRTs)
            {
                name2nodesMap[refPRT.ProjectName] = graph.Nodes.GetOrCreate(refPRT.ProjectName);
                name2nodesMap[refPRT.ProjectName].Label = refPRT.ProjectName;
            }

            // Create a link between the nodes
            foreach (var refPRT in prt.AllPRTs)
            {
                foreach (var refProj in refPRT.References)
                {
                    var link = graph.Links.GetOrCreate(name2nodesMap[refPRT.ProjectName], name2nodesMap[refProj.ProjectName]);
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
                foreach (var item in prt.AllPRTs)
                {
                    // An isolated project still deserves a node of its own.
                    if (item.References.Count == 0)
                        writer.WriteLine("    {0}", DotId(item.ProjectName));

                    foreach (var subItem in item.References)
                    {
                        writer.WriteLine("    {0} -> {1}",
                            DotId(item.ProjectName), DotId(subItem.ProjectName));
                    }
                }
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
                    p.StartInfo.FileName = "dot";
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

        /// <summary>True when Graphviz's 'dot' can be found, so the SVG/PNG options are usable.</summary>
        public static bool IsGraphvizAvailable()
        {
            return FindOnPath("dot.exe") != null;
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

            foreach (var proj in AllProjects)
            {
                name2nodesMap[proj.Name] = graph.Nodes.GetOrCreate(proj.Name);
                name2nodesMap[proj.Name].Label = proj.Name;
            }

            // Create a link between the nodes
            foreach (var item in AllProjects)
            {
                foreach (var refProj in item.ProjectReferences())
                {
                    var link = graph.Links.GetOrCreate(name2nodesMap[item.Name], name2nodesMap[refProj.Name]);
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
