using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DependencyGraph.State
{
    /// <summary>
    /// The Tools -> Options page. Registered by <c>DependencyGraphPackage</c>.
    /// </summary>
    /// <remarks>
    /// Adding this page also needs a <c>ToolsOptionsPages</c> entry in the checked-in
    /// <c>DependencyGraph.pkgdef</c>, because that file is maintained by hand - see the comments
    /// at the top of it.
    /// </remarks>
    [ComVisible(true)]
    [Guid("9c1a5f2e-6b84-4d17-9a3f-7c58e0d21b46")]
    public class OptionsPage : BaseOptionPage<Settings> { }

    /// <summary>Persisted settings, written by the Options page.</summary>
    public class Settings : BaseOptionModel<Settings>
    {
        [Category("Defaults")]
        [DisplayName("View")]
        [Description("Which references to draw when the window opens.")]
        [DefaultValue(DefaultView.DeepestReferencesOnly)]
        public DefaultView View { get; set; } = DefaultView.DeepestReferencesOnly;

        [Category("Defaults")]
        [DisplayName("Direction")]
        [Description("Direction the reference depth grows in when the window opens.")]
        [DefaultValue(DefaultDirection.TopToBottom)]
        public DefaultDirection Direction { get; set; } = DefaultDirection.TopToBottom;

        [Category("Defaults")]
        [DisplayName("Depth")]
        [Description("How many levels of references to draw when the window opens. 0 means no limit.")]
        [DefaultValue(0)]
        public int Depth { get; set; } = 0;

        [Category("Defaults")]
        [DisplayName("Fit the graph to the window on open")]
        [Description("Off opens at 100%, which keeps the node text crisp. On fits the whole graph in the window instead.")]
        [DefaultValue(false)]
        public bool FitOnOpen { get; set; } = false;

        [Category("Appearance")]
        [DisplayName("Node label font size")]
        [Description("Point size of the project names in the graph, 6 to 32. Nodes are sized around their label, so this scales the whole graph.")]
        [DefaultValue(14.0)]
        public double LabelFontSize { get; set; } = 14.0;

        [Category("Appearance")]
        [DisplayName("Show the target framework on each node")]
        [Description("Adds each project's target framework under its name in the graph. Off by default because it makes every node taller and wider, which is a real cost on a large solution - the framework is always in the node's tooltip and in the status bar.")]
        [DefaultValue(false)]
        public bool ShowTargetFramework { get; set; } = false;

        [Category("Filtering")]
        [DisplayName("Excluded projects")]
        [Description("Semicolon-separated name patterns to leave out of the graph, for example \"*.Tests;*.Benchmarks\". Matched against the project name, case-insensitively.")]
        [DefaultValue("")]
        public string ExcludedProjects { get; set; } = "";

        [Category("Graphviz")]
        [DisplayName("Path to dot.exe")]
        [Description("Full path to Graphviz's dot.exe. Leave empty to search the PATH. Only needed for the Graphviz SVG and PNG exports.")]
        [DefaultValue("")]
        public string GraphvizDotPath { get; set; } = "";
    }

    public enum DefaultView
    {
        [Description("Deepest references only")] DeepestReferencesOnly,
        [Description("All references")]          AllReferences,
        [Description("Project references only")] ProjectReferencesOnly,
        [Description("Referenced by (impact)")]  ReferencedBy,
    }

    public enum DefaultDirection
    {
        [Description("Top to bottom")] TopToBottom,
        [Description("Left to right")] LeftToRight,
        [Description("Bottom to top")] BottomToTop,
        [Description("Right to left")] RightToLeft,
    }
}
