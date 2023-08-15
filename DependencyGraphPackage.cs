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
    [Guid(PackageGuids.DependencyGraphString)]
    public sealed class DependencyGraphPackage : ToolkitPackage
    {
        private static IServiceProvider serviceProvider;
        private static DTE2 dte;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await this.RegisterCommandsAsync();
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var shellService = await this.GetServiceAsync(typeof(SVsShell)) as IVsShell;
            if (shellService != null)
            {
                InitializeServices();
            }
        }

        private void InitializeServices()
        {
            dte = this.GetService<SDTE, SDTE>() as DTE2;

            Debug.Assert(dte != null, "dte != null");
            if (dte != null)
            {
                DependencyGraphPackage.serviceProvider = this;
            }
            else
            {
                Debug.WriteLine("[DependencyGraph] Cannot get a DTE service.");
            }
        }

        internal static IServiceProvider GetServiceProvider()
        {
            return serviceProvider;
        }

        internal static DTE2 GetDTE()
        {
            return dte;
        }
    }
}