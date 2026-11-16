using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Oadm.Plugins.VpnFleet.Client;

public sealed partial class VpnFleetView : UserControl
{
    public VpnFleetView() => AvaloniaXamlLoader.Load(this);

    // The pickers and the wizards need the window: the view hands the view model a way to open them.
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not VpnFleetViewModel vm)
        {
            return;
        }

        vm.PickDevices = async () => TopLevel.GetTopLevel(this) is Window owner && await vm.Devices.PickAsync(owner, "Choose devices");
        vm.PickProfile = () => TopLevel.GetTopLevel(this) is { } top ? ProfileFiles.PickAsync(top) : Task.FromResult<(string, string)?>(null);
        vm.OpenUrl = OpenUrl;
        vm.ShowWizard = ShowWizardAsync;
    }

    private async Task ShowWizardAsync(RolloutWizardViewModel wizard)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        if (wizard.Install is { } install)
        {
            install.OpenUrl = OpenUrl;
        }

        var window = new RolloutWizardWindow();
        window.Attach(wizard);
        await window.ShowDialog(owner);
    }

    /// <summary>Opens a release page of the apps in the browser; nothing but GitHub.</summary>
    private void OpenUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && GitHubHttp.IsGitHub(uri) && TopLevel.GetTopLevel(this) is { } top)
        {
            _ = top.Launcher.LaunchUriAsync(uri);
        }
    }

    // The host disposes the view model when the page or its window closes; that ends the watch.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as VpnFleetViewModel)?.Start();
    }
}
