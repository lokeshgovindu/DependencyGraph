global using Community.VisualStudio.Toolkit;
global using Microsoft.VisualStudio.Shell;
global using System;
global using Task = System.Threading.Tasks.Task;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using VSLangProj2;

namespace DependencyGraph
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration(Vsix.Name, Vsix.Description, Vsix.Version)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
    // MDI puts the graph in the document area in the middle, where it has room, rather than
    // tabbing it into the narrow Solution Explorer dock.
    [ProvideToolWindow(typeof(UI.DependencyGraphWindow.Pane), Style = VsDockStyle.MDI)]
    // Tools -> Options -> DependencyGraph -> General. The matching ToolsOptionsPages entry has to
    // be added to DependencyGraph.pkgdef by hand, since that file is checked in rather than
    // generated - see the comments at the top of it.
    [ProvideOptionPage(typeof(State.OptionsPage), "DependencyGraph", "General", 0, 0, true,
        SupportsProfiles = true)]
    [Guid(PackageGuids.DependencyGraphString)]
    public sealed class DependencyGraphPackage : ToolkitPackage
    {
        private static IServiceProvider _serviceProvider;
        private static DTE2 _dte;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await this.RegisterCommandsAsync();
            UI.DependencyGraphWindow.Initialize(this);
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var shellService = await this.GetServiceAsync(typeof(SVsShell)) as IVsShell;
            if (shellService != null)
            {
                InitializeServices();
            }
        }

        private void InitializeServices()
        {
            _dte = this.GetService<SDTE, SDTE>() as DTE2;

            Debug.Assert(_dte != null, "dte != null");
            if (_dte != null)
            {
                DependencyGraphPackage._serviceProvider = this;
            }
            else
            {
                Debug.WriteLine("[DependencyGraph] Cannot get a DTE service.");
            }
        }

        internal static IServiceProvider GetServiceProvider()
        {
            return _serviceProvider;
        }

        internal static DTE2 GetDTE()
        {
            return _dte;
        }
    }
}