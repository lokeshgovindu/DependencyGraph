using DependencyGraph.Scan;
using DependencyGraph.UI;
using EnvDTE80;
using Microsoft.VisualStudio.Debugger.Interop;
using System.ComponentModel.Design;
using System.Windows;

namespace DependencyGraph
{
    [Command(PackageIds.DependencyGraph)]
    internal sealed class DependencyGraph : BaseCommand<DependencyGraph>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            DTE2 dte = DependencyGraphPackage.GetDTE();
            MainWindow mainWindow = new MainWindow(dte);
            mainWindow.Show();
        }
    }
}
