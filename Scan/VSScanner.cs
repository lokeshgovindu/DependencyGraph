using EnvDTE;
using EnvDTE80;
using System;
using System.Collections.Generic;
using VSLangProj;
using Project = EnvDTE.Project;
using Reference = VSLangProj.Reference;

namespace DependencyGraph.Scan
{
    /// <summary>
    /// Enumerates the projects in the open solution, flattening solution folders.
    /// </summary>
    public class VSScanner
    {
        private List<Project> _projects = new List<Project>();
        private bool _scanned = false;

        public List<Project> Projects           { get => _projects; }
        public DTE2          _dte               { get; set; }
        public string        SolutionName       { get; set; }
        public string        StartupProjectName { get; set; }
        public Project       StartupProject     { get; set; }

        public VSScanner(_DTE dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _dte = dte as DTE2;
            Run();
            _scanned = true;
            if (StartupProjectName != null) StartupProject = GetProject(StartupProjectName);

            // The startup project can be missing, unloaded, or of a kind we do not enumerate,
            // so fall back to the first project rather than handing back null.
            if (StartupProject == null && _projects.Count > 0) StartupProject = _projects[0];
        }

        public void Run()
        {
            if (_scanned) return;
            ThreadHelper.ThrowIfNotOnUIThread();

            SolutionName = _dte.Solution.FullName;

            // A solution need not have a startup project set, and for some solution types
            // StartupProjects comes back null or empty - which used to throw here.
            var startupProjects = _dte.Solution.SolutionBuild?.StartupProjects as Array;
            if (startupProjects != null && startupProjects.Length > 0)
                StartupProjectName = startupProjects.GetValue(0)?.ToString();

            foreach (Project p in _dte.Solution.Projects)
                ExpandProjectsFolder(p, _projects);
        }

        public Project GetProject(string projectName)
        {
            return _projects.Find(p =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return p.Name == projectName || p.UniqueName == projectName;
            });
        }

        #region Scanning

        /// <summary>
        /// The solution folder a project sits in, or null for one at the solution root.
        /// </summary>
        /// <remarks>
        /// Recorded while flattening, because the flattened project list on its own loses it and
        /// the graph uses it to group nodes.
        /// </remarks>
        public string SolutionFolderOf(string projectKey)
        {
            return projectKey != null && _folders.TryGetValue(projectKey, out var folder)
                ? folder
                : null;
        }

        private readonly Dictionary<string, string> _folders = new Dictionary<string, string>();

        private void ExpandProjectsFolder(Project p, List<Project> pros, string folder = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (IsProject(p))
            {
                pros.Add(p);
                if (!string.IsNullOrEmpty(folder)) _folders[ProjectReferenceTree.KeyOf(p)] = folder;
                return;
            }

            // Solution folders nest, so recurse. ProjectItems can be absent for some kinds.
            ProjectItems items;
            try { items = p.ProjectItems; }
            catch { return; }
            if (items == null) return;

            for (int i = 1; i <= items.Count; ++i)
            {
                Project sp;
                try { sp = items.Item(i).Object as Project; }
                catch { continue; }
                if (sp == null) continue;

                // Solution folders nest, so build up a path like "Components/Data".
                string nested = string.IsNullOrEmpty(folder) ? p.Name : folder + "/" + p.Name;
                ExpandProjectsFolder(sp, pros, nested);
            }
        }

        const string PROJECT_FOLDERS     = "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}";
        const string SOLUTION_FOLDER     = "{2150E333-8FDC-42A3-9474-1A3956D46DE8}";
        const string MISCELLANEOUS_FILES = "{66A2671D-8FB5-11D2-AA7E-00C04F688DDE}";

        // Ref: https://github.com/JamesW75/visual-studio-project-type-guid
        static bool IsProject(Project p)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            bool notAProject = (
                p.Kind == PROJECT_FOLDERS ||
                p.Kind == SOLUTION_FOLDER ||
                p.Kind == MISCELLANEOUS_FILES
            );
            return !notAProject;
        }

        #endregion
    }

    public static class ProjectExtensions
    {
        /// <summary>
        /// The project's target framework, in the form a person would recognise, or null when the
        /// project will not say.
        /// </summary>
        /// <remarks>
        /// From the TargetFrameworkMoniker property, which is what the project system answers with
        /// for C#, VB and F# - ".NETFramework,Version=v4.7.2", ".NETCoreApp,Version=v8.0" and so
        /// on. Plenty of project kinds do not have the property at all and throw when asked, which
        /// is not worth reporting: it just means there is nothing to show.
        ///
        /// A project that multi-targets reports only one framework here. The moniker property is
        /// singular, and the several targets live in the project file's TargetFrameworks, which
        /// automation does not surface - so this says "net472" for a project that is also building
        /// net8.0, rather than pretending to know both.
        /// </remarks>
        public static string TargetFramework(this Project p)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string moniker = null;
            try { moniker = p.Properties?.Item("TargetFrameworkMoniker")?.Value as string; }
            catch { return null; }

            return FriendlyFramework(moniker);
        }

        /// <summary>Turns a target framework moniker into something worth putting on screen.</summary>
        internal static string FriendlyFramework(string moniker)
        {
            if (string.IsNullOrWhiteSpace(moniker)) return null;

            // "<identifier>,Version=v<version>[,Profile=<profile>]"
            var parts = moniker.Split(',');
            string identifier = parts[0].Trim();
            string version = null;
            string profile = null;

            foreach (var part in parts)
            {
                var pair = part.Split(new[] { '=' }, 2);
                if (pair.Length != 2) continue;

                if (pair[0].Trim().Equals("Version", StringComparison.OrdinalIgnoreCase))
                    version = pair[1].Trim().TrimStart('v', 'V');
                else if (pair[0].Trim().Equals("Profile", StringComparison.OrdinalIgnoreCase))
                    profile = pair[1].Trim();
            }

            string name;
            switch (identifier)
            {
                case ".NETFramework": name = ".NET Framework"; break;
                case ".NETStandard":  name = ".NET Standard"; break;
                case ".NETPortable":  name = ".NET Portable"; break;
                case "Silverlight":   name = "Silverlight"; break;

                // The moniker stayed .NETCoreApp when the product was renamed at version 5, so
                // the version is the only thing that says which name to use.
                case ".NETCoreApp":   name = MajorVersion(version) >= 5 ? ".NET" : ".NET Core"; break;

                default:              name = identifier.TrimStart('.'); break;
            }

            string text = version == null ? name : name + " " + version;
            return profile == null ? text : text + " (" + profile + ")";
        }

        private static int MajorVersion(string version)
        {
            if (string.IsNullOrEmpty(version)) return 0;

            var major = version.Split('.')[0];
            return int.TryParse(major, out int n) ? n : 0;
        }

        public static List<Project> ProjectReferences(this Project p)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var projects = new List<Project>();

            // Only VSLangProj projects (C#, VB, F#) expose references this way. C++, database,
            // shared and unloaded projects hand back null, so they simply have no references we
            // can read - they used to crash here with a NullReferenceException.
            VSProject vsProject = p.Object as VSProject;
            if (vsProject == null) return projects;

            References references;
            try
            {
                references = vsProject.References;
            }
            catch
            {
                // An unloaded or broken project throws rather than returning null.
                return projects;
            }
            if (references == null) return projects;

            foreach (Reference projRef in references)
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
