using System.Text.Json;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;

namespace Oadm.Plugins.VpnFleet.Client;

/// <summary>The first step of the install wizard: the app and its newest release on GitHub.</summary>
public sealed partial class InstallViewModel : ObservableObject
{
    private readonly ICorePluginClientContext _ctx;
    private readonly CancellationToken _stop;
    private Dictionary<VpnKind, ReleaseInfo> _releases = [];

    public InstallViewModel(ICorePluginClientContext ctx, CancellationToken stop)
    {
        _ctx = ctx;
        _stop = stop;
        SelectedApp = Apps[0];
    }

    public IReadOnlyList<VpnChoice> Apps { get; } = [.. VpnApps.All.Select(a => new VpnChoice(a.Kind, a.DisplayName))];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatestVersion))]
    public partial VpnChoice SelectedApp { get; set; }

    public Action<string>? OpenUrl { get; set; }

    [ObservableProperty]
    public partial string ReleaseText { get; private set; } = "";

    [ObservableProperty]
    public partial string? ReleaseUrl { get; private set; }

    [ObservableProperty]
    public partial bool IsChecking { get; private set; }

    [ObservableProperty]
    public partial string? ErrorText { get; private set; }

    public string? LatestVersion => _releases.GetValueOrDefault(SelectedApp.Kind)?.Version;

    partial void OnSelectedAppChanged(VpnChoice value) => ShowRelease();

    /// <summary>Reads the newest releases (from the server's cache unless <paramref name="checkNow"/>).</summary>
    public async Task LoadReleasesAsync(bool checkNow)
    {
        IsChecking = true;
        ShowRelease();
        try
        {
            string? reply = null;
            ErrorText = await GuardedRun.RunAsync(
                async () => reply = await _ctx.InvokeAsync(Fleet.GetReleases, JsonSerializer.Serialize(new ReleasesRequest(checkNow), Fleet.Json), _stop),
                GuardedCancel.Ignore);
            if (reply is not null && JsonSerializer.Deserialize<List<ReleaseInfo>>(reply, Fleet.Json) is { } list)
            {
                _releases = list.ToDictionary(r => r.Kind);
            }
        }
        finally
        {
            IsChecking = false;
            ShowRelease();
            OnPropertyChanged(nameof(LatestVersion));
        }
    }

    private void ShowRelease()
    {
        var release = _releases.GetValueOrDefault(SelectedApp.Kind);
        ReleaseUrl = release?.PageUrl;
        ReleaseText = release switch
        {
            null => IsChecking ? "Checking GitHub for the newest version." : "",
            { Version: null } => release.Error ?? "The newest version is not known.",
            _ => $"Newest version: {release.Version}" + (release.Published is { } at ? $", released {at.LocalDateTime:d}." : "."),
        };
    }

    [RelayCommand]
    private Task CheckNowAsync() => LoadReleasesAsync(true);

    [RelayCommand]
    private void OpenRelease()
    {
        if (ReleaseUrl is { } url)
        {
            OpenUrl?.Invoke(url);
        }
    }
}
