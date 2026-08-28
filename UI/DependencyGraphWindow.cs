using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace DependencyGraph.UI
{
    /// <summary>
    /// The dockable tool window hosting <see cref="DependencyGraphControl"/>. Replaces the modal
    /// dialog the extension used to open, which blocked the whole IDE while it was up.
    /// </summary>
    internal class DependencyGraphWindow : BaseToolWindow<DependencyGraphWindow>
    {
        public override System.Type PaneType => typeof(Pane);

        public override string GetTitle(int toolWindowId) => "Dependency Graph";

        public override async System.Threading.Tasks.Task<FrameworkElement> CreateAsync(
            int toolWindowId, CancellationToken cancellationToken)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var control = new DependencyGraphControl();
            control.Initialize(DependencyGraphPackage.GetDTE());
            return control;
        }

        [Guid("6f2b8a41-9d3c-4a15-8b7e-2c9d4f1a6e83")]
        internal class Pane : ToolWindowPane
        {
            public Pane()
            {
                BitmapImageMoniker = KnownMonikers.GraphLeftToRight;
            }
        }
    }
}
