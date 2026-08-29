using DependencyGraph.Scan;
using Project = EnvDTE.Project;

namespace DependencyGraph.UI
{
    /// <summary>
    /// One entry in the project picker.
    /// </summary>
    /// <remarks>
    /// The picker used to hold bare name strings, which made two projects sharing a name in
    /// different solution folders indistinguishable - selecting either one showed whichever the
    /// scan happened to find first. Carrying the stable key alongside the display name settles
    /// which project was actually chosen. <see cref="ToString"/> is what the combo shows, and
    /// what its type-to-search matches against.
    /// </remarks>
    internal class ProjectChoice
    {
        public string Name { get; }
        public string Key { get; }
        public Project Project { get; }

        public ProjectChoice(Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Project = project;
            Name = project.Name;
            Key = ProjectReferenceTree.KeyOf(project);
        }

        /// <summary>
        /// Shown in the combo. Only disambiguated when it has to be, so the common case stays
        /// clean: a bare name if it is unique, otherwise the name plus its solution-relative path.
        /// </summary>
        public bool ShowKey { get; set; }

        public override string ToString()
        {
            return ShowKey ? $"{Name}  ({Key})" : Name;
        }
    }
}
