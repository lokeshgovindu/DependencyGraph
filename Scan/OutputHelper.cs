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
                    foreach (var subItem in item.References)
                    {
                        writer.WriteLine("    {0} -> {1}", item.ProjectName, subItem.ProjectName);
                    }
                }
                writer.WriteLine("}");
            }
            return true;
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
            return CreateGraphvizOutput(dotFilePath, outputFormat, outputFilePath);
        }

        internal static bool CreateGraphvizOutput(string inputDotFilePath, string outputFormat, string outputFilePath)
        {
            try
            {
                string args = String.Format("-T{0} -o{1} {2}", outputFormat, outputFilePath, inputDotFilePath);
                using (var p = new System.Diagnostics.Process())
                {
                    p.StartInfo.UseShellExecute = false;
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

        private static string GetTempFilePath(string ext)
        {
            return System.IO.Path.GetTempPath() + Guid.NewGuid().ToString() + "." + ext;
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
                    foreach (var subItem in item.ProjectReferences())
                    {
                        writer.WriteLine("    {0} -> {1}", item.Name, subItem.Name);
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
            return CreateGraphvizOutput(dotFilePath, "svg", outputFilePath);
        }

        internal static bool CreateSolutionGraphvizPNG(VSScanner vsScanner, string outputFilePath)
        {
            string dotFilePath = outputFilePath + ".dot";
            CreateSolutionGraphvizDot(vsScanner, dotFilePath);
            return CreateGraphvizOutput(dotFilePath, "png", outputFilePath);
        }
    }
}
