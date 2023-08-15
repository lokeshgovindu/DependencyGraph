using EnvDTE;
using EnvDTE80;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using VSLangProj;
using VSLangProj80;
using static System.Console;
using Project = EnvDTE.Project;
using Reference = VSLangProj.Reference;

namespace DependencyGraph.Scan
{
    public class VSScanner
    {
        private List<Project> _projects = new List<Project>();
        private List<VSProject2> _vsprojects = new List<VSProject2>();
        private bool _scanned = false;

        public List<Project> Projects { get => _projects; }
        public List<VSProject2> Vsprojects { get => _vsprojects; }
        public DTE2 _dte { get; set; }
        public string SolutionName { get; set; }
        public string StartupProjectName { get; set; }
        public Project StartupProject { get; set; }

        public VSScanner(_DTE dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _dte = dte as DTE2;
            Run();
            _scanned = true;
            StartupProject = GetProject(StartupProjectName);
        }

        public void Run()
        {
            if (_scanned) return;
            ThreadHelper.ThrowIfNotOnUIThread();

            SolutionName = _dte.Solution.FullName;
            StartupProjectName = ((Array)_dte.Solution.SolutionBuild.StartupProjects).GetValue(0).ToString();

            var items = _dte.Solution.Projects;
            foreach (Project p in items)
                ExpandProjectsFolder(p, _projects);

            for (int i = 0; i < _projects.Count; i++)
            {
                //WriteLine($@"project: {p.Name}");
                WriteLine("{0}. {1,-24} - {2}", i + 1, _projects[i].Name, _projects[i].FullName);
                var vsp = _projects[i].Object as VSProject2;
                _vsprojects.Add(vsp);
            }
        }

        public ProjectReferenceTree GetProjectReferenceTree(string projectName)
        {
            Run();

            var p = GetProject(projectName);
            if (p == null)
            {
                if (_dte != null) WriteLine($@"Couldn't find project named ""{projectName}"" in ""{SolutionName}"" solution.");
                return null;
            }
            ProjectReferenceTree.toString = (r) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return r.Item.Name;
            };
            return new ProjectReferenceTree(p);
        }

        public ProjectReferenceTree GetProjectReferencesOnly(string projectName)
        {
            Run();

            var p = GetProject(projectName);
            if (p == null)
            {
                if (_dte != null) WriteLine($@"Couldn't find project named ""{projectName}"" in ""{SolutionName}"" solution.");
                return null;
            }
            ProjectReferenceTree.toString = (r) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return r.Item.Name;
            };
            return ProjectReferenceTree.GetProjectReferencesOnly(p);
        }

        public Project GetProject(string projectName)
        {
            return _projects.Find(p =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return p.Name == projectName || p.UniqueName == projectName;
            });
        }

        private void PrintReferences(VSProject2 vsp)
        {
            if (vsp == null) return;
            foreach (Reference r in vsp.References)
            {
                if (r.SourceProject == null) continue;
                WriteLine($@"	reference: {r.Name}");
                //WriteLine($@"		type: {r.SourceProject?.Name}");
            }
        }

        #region Scanning
        private bool ExpandProjectsFolder(Project p, List<Project> pros)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            //Console.Out.WriteLine("{0,-21}\t{1}\t{2}", p.Name, p.Kind, p.UniqueName);
            if (IsProject(p)) { pros.Add(p); return false; }
            for (int i = 1; i <= p.ProjectItems.Count; ++i)
            {
                Project sp = p.ProjectItems.Item(i).Object as Project;
                if (sp == null) continue;
                ExpandProjectsFolder(sp, pros);
            }
            return true;
        }

        private bool IsProject(Project p)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            //var pn = p.Name; 
            //return p.Kind == "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}"; // Project Folders
            return p.Kind == "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}" ||    // C#
                p.Kind == "{8BC9CEB8-8B4A-11D0-8D11-00A0C91BC942}";  // C++
        }
        #endregion
    }

    public static class ProjectExtensions
    {

        public static List<Project> ProjectReferences(this Project p)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var l = new List<Project>();
            Debug.WriteLine($"cheking references of: {p.Name}");
            foreach (Reference r in p.ass<VSProject>().References)
            {
                if (r.SourceProject == null) continue;
                l.Add(r.SourceProject);
            }
            return l;
        }

        public static T ass<T>(this Project p) where T : class
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return p.Object as T;
        }
    }

}
