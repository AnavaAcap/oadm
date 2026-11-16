using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Oadm.Plugins.VpnFleet.Client;

public sealed partial class VpnFleetView : UserControl
{
    public VpnFleetView() => AvaloniaXamlLoader.Load(this);

    // The pickers, the browser and the wizards need the window: the view hands the view model a way to open them.
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not VpnFleetViewModel vm)
        {
            return;
        }

        vm.PickDevices = async () => TopLevel.GetTopLevel(this) is Window owner && await vm.Devices.PickAsync(owner, "Choose devices");
        vm.PickProfile = () => TopLevel.GetTopLevel(this) is { } top ? ProfileFiles.PickAsync(top) : Task.FromResult<ProfileFile?>(null);
        vm.Launch = async uri =>
        {
            if (TopLevel.GetTopLevel(this) is { } top)
            {
                await top.Launcher.LaunchUriAsync(uri);
            }
        };
        vm.ShowWizard = ShowWizardAsync;
    }

    private async Task ShowWizardAsync(RolloutWizardViewModel wizard)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var window = new RolloutWizardWindow();
        window.Attach(wizard);
        await window.ShowDialog(owner);
    }

    // The host disposes the view model when the page or its window closes; that ends the watch.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as VpnFleetViewModel)?.Start();
    }
}
