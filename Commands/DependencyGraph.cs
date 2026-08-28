using DependencyGraph.UI;

namespace DependencyGraph
{
    [Command(PackageIds.Show)]
    internal sealed class DependencyGraph : BaseCommand<DependencyGraph>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await DependencyGraphWindow.ShowAsync();
        }
    }
}
