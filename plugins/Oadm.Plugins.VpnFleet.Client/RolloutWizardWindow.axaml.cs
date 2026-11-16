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

        if (viewModel.Install is { } install)
        {
            install.PickPackages = () => PackageFiles.PickAsync(this);
        }

        viewModel.CloseRequested += (_, _) => Close();
    }
}

/// <summary>The file dialog of an OpenVPN profile, or null when cancelled. A file too large for a profile is not read.</summary>
internal static class ProfileFiles
{
    public static async Task<ProfileFile?> PickAsync(TopLevel top)
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
            return new ProfileFile(files[0].Name, stream.Length, null);
        }

        using var reader = new StreamReader(stream);
        var text = await reader.ReadToEndAsync();
        return new ProfileFile(files[0].Name, text.Length, text);
    }
}

/// <summary>The file dialog of .eap packages: their local paths (none when cancelled or not on a local disk).</summary>
internal static class PackageFiles
{
    public static async Task<IReadOnlyList<string>> PickAsync(TopLevel top)
    {
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose the packages",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("ACAP package") { Patterns = ["*.eap"] }],
        });
        return [.. files.Select(f => f.TryGetLocalPath()).OfType<string>()];
    }
}
