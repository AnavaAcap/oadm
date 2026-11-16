using System.Collections.ObjectModel;
using System.Text.Json;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Acap;
using Oadm.Sdk.Client;

namespace Oadm.Plugins.VpnFleet.Client;

/// <summary>A package chosen on this computer: its path and what it says about itself.</summary>
public sealed record LocalPackageItem(string Path, string Name, EapManifest Package)
{
    /// <summary>"Tailscale VPN 1.104.7, aarch64".</summary>
    public string Text => $"{Package.DisplayName} {Package.Version}, {(string.IsNullOrWhiteSpace(Package.Architecture) ? "any architecture" : Package.Architecture)}";
}

/// <summary>
/// The first step of the install wizard: the app, and where it comes from: its newest release on GitHub, or the packages
/// of a release the user downloaded (for an OADM without internet).
/// </summary>
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

    /// <summary>The packages come from this computer, not from GitHub.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGitHub), nameof(LatestVersion), nameof(SourceKey), nameof(BlockedReason))]
    public partial bool FromFiles { get; set; }

    public bool IsGitHub => !FromFiles;

    public ObservableCollection<LocalPackageItem> Packages { get; } = [];

    /// <summary>Why the chosen packages cannot be used, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatestVersion), nameof(BlockedReason))]
    public partial string? PackagesError { get; private set; }

    /// <summary>Opens a file dialog for .eap files: their local paths. Set by the window.</summary>
    public Func<Task<IReadOnlyList<string>>>? PickPackages { get; set; }

    /// <summary>The version the wizard installs: of the packages, or of the GitHub release.</summary>
    public string? LatestVersion => FromFiles
        ? (Packages.Count > 0 && PackagesError is null ? Packages[0].Package.Version : null)
        : _releases.GetValueOrDefault(SelectedApp.Kind)?.Version;

    /// <summary>The builds of the packages, for the choice per camera (key: the local path).</summary>
    public IReadOnlyList<AcapBuild> Builds => [.. Packages.Select(p => new AcapBuild(p.Path, p.Name, p.Package))];

    /// <summary>Changes when the source or the packages change: the cameras step lists the cameras again.</summary>
    public string SourceKey => FromFiles ? "files|" + string.Join('|', Packages.Select(p => p.Path)) : "github";

    /// <summary>Why the wizard cannot go on from this step; null when it can.</summary>
    public string? BlockedReason => FromFiles
        ? (Packages.Count == 0 ? "Choose the packages." : PackagesError)
        : LatestVersion is null ? (IsChecking ? "Checking GitHub." : "The newest version is not known.") : null;

    partial void OnSelectedAppChanged(VpnChoice value)
    {
        ShowRelease();
        CheckPackages();
    }

    [RelayCommand]
    private async Task ChoosePackagesAsync()
    {
        if (PickPackages is not { } pick)
        {
            return;
        }

        foreach (var path in await pick())
        {
            if (Packages.Any(p => string.Equals(p.Path, path, StringComparison.Ordinal)))
            {
                continue;
            }

            try
            {
                Packages.Add(new LocalPackageItem(path, System.IO.Path.GetFileName(path), await EapReader.ReadAsync(path, _stop)));
            }
            catch (Exception ex) when (ex is InvalidEapException or IOException or UnauthorizedAccessException)
            {
                PackagesError = $"{System.IO.Path.GetFileName(path)}: {ex.Message}";
                return;
            }
        }

        CheckPackages();
    }

    [RelayCommand]
    private void UseGitHub() => FromFiles = false;

    [RelayCommand]
    private void RemovePackage(LocalPackageItem? item)
    {
        if (item is not null && Packages.Remove(item))
        {
            CheckPackages();
        }
    }

    /// <summary>The server's rule (one app, one version) before anything is uploaded.</summary>
    private void CheckPackages()
    {
        PackagesError = Packages.Count == 0 ? null : LocalPackages.Problem(VpnApps.Of(SelectedApp.Kind), [.. Packages.Select(p => (p.Name, p.Package))]);
        OnPropertyChanged(nameof(SourceKey));
        OnPropertyChanged(nameof(LatestVersion));
        OnPropertyChanged(nameof(BlockedReason));
    }

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
            OnPropertyChanged(nameof(BlockedReason));
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
