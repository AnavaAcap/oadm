using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.VpnFleet.Client;

/// <summary>The wizard of an action on many cameras; closes when the view model asks. The pickers open over this window.</summary>
public partial class RolloutWizardWindow : Window
{
    public RolloutWizardWindow() => AvaloniaXamlLoader.Load(this);

    public void Attach(RolloutWizardViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        if (viewModel.Form is { } form)
        {
            form.PickHub = selected => DevicePickerWindow.ShowOneAsync(this, viewModel.HubCandidates?.Invoke() ?? [], selected, heading: "Choose the hub camera");
            form.PickProfile = () => ProfileFiles.PickAsync(this);
        }

        viewModel.CloseRequested += (_, _) => Close();
    }
}

/// <summary>The file dialog of an OpenVPN profile: its name and text, or null when cancelled.</summary>
internal static class ProfileFiles
{
    public static async Task<(string Name, string Text)?> PickAsync(TopLevel top)
    {
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose the VPN profile",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("OpenVPN profile") { Patterns = ["*.ovpn", "*.conf"] }],
        });
        if (files.Count == 0)
        {
            return null;
        }

        await using var stream = await files[0].OpenReadAsync();
        if (stream.CanSeek && stream.Length > ConnectRules.MaxProfileLength)
        {
            return (files[0].Name, new string('#', ConnectRules.MaxProfileLength + 1)); // the rule then shows "larger than 256 KB"
        }

        using var reader = new StreamReader(stream);
        return (files[0].Name, await reader.ReadToEndAsync());
    }
}
