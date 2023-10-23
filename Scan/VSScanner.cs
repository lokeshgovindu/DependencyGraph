using EnvDTE;
using EnvDTE80;
using System.Collections.Generic;
using VSLangProj;
using VSLangProj80;
using static System.Console;
using Project = EnvDTE.Project;
using Reference = VSLangProj.Reference;

namespace DependencyGraph.Scan
{
    public class VSScanner
    {
        private List<Project>       _projects   = new List<Project>();
        private List<VSProject2>    _vsprojects = new List<VSProject2>();
        private bool                _scanned    = false;

        public List<Project>        Projects            { get => _projects; }
        public List<VSProject2>     Vsprojects          { get => _vsprojects; }
        public DTE2                 _dte                { get; set; }
        public string               SolutionName        { get; set; }
        public string               StartupProjectName  { get; set; }
        public Project              StartupProject      { get; set; }

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

        const string PROJECT_FOLDERS     = "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}";
        const string SOLUTION_FOLDER     = "{2150E333-8FDC-42A3-9474-1A3956D46DE8}";
        const string MISCELLANEOUS_FILES = "{66A2671D-8FB5-11D2-AA7E-00C04F688DDE}";

        // Ref: https://github.com/JamesW75/visual-studio-project-type-guid
        static bool IsProject(Project p)
        {
            bool notAProject = (
                p.Kind == PROJECT_FOLDERS ||
                p.Kind == SOLUTION_FOLDER ||
                p.Kind == MISCELLANEOUS_FILES
            );
            return !notAProject;

            //return
            //    p.Kind == "{8BC9CEB8-8B4A-11D0-8D11-00A0C91BC942}" ||   // C++
            //    p.Kind == "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}" ||   // C#
            //    p.Kind == "{F184B08F-C81C-45F6-A57F-5ABD9991F28F}";     // VB
        }
        #endregion
    }

    public static class ProjectExtensions
    {
        public static List<Project> ProjectReferences(this Project p)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var projects = new List<Project>();
            VSProject vsProject = p.Object as VSProject;
            foreach (Reference projRef in vsProject.References)
            {
                if (projRef == null) continue;
                try
                {
                    if (projRef.SourceProject == null) continue;
                }
                catch { continue; }
                projects.Add(projRef.SourceProject);
            }
            return projects;
        }
    }
}
