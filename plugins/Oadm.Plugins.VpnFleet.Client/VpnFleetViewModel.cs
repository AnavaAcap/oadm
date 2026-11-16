using System.Text.Json;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Acap;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.VpnFleet.Client;

/// <summary>The panel right of the table.</summary>
public enum FleetPanel
{
    None,
    Camera,
    Peers,
}

/// <summary>
/// The page: the VPN apps of the chosen devices, their tunnels and versions. A row opens its camera's settings; Install
/// or upgrade and Set up many cameras open a wizard with the cameras in a table. Reads the state from the server part,
/// then follows its events. Nothing is read before a device choice.
/// </summary>
public sealed partial class VpnFleetViewModel : ObservableObject, IDisposable
{
    private readonly ICorePluginClientContext _ctx;
    private readonly CancellationTokenSource _stop = new();
    private Task? _run;
    private int _version = -1;
    private Dictionary<Guid, List<FleetRow>> _rowsByDevice = [];

    public VpnFleetViewModel(ICorePluginClientContext ctx)
    {
        _ctx = ctx;
        Devices = new PageDeviceChoice(ctx);
        Devices.PropertyChanged += (_, _) => RefreshCommand.NotifyCanExecuteChanged();
        MayConnect = ctx.Has(PermissionKeys.Of(Fleet.PluginId, Fleet.ConnectPermission));
        MayInstall = ctx.Has(PermissionKeys.Of(Fleet.PluginId, Fleet.InstallPermission));
    }

    public PageDeviceChoice Devices { get; }

    /// <summary>Opens the device picker; set by the view, which knows the window.</summary>
    public Func<Task<bool>>? PickDevices { get; set; }

    /// <summary>Shows a wizard over the page's window until it closes; set by the view.</summary>
    public Func<RolloutWizardViewModel, Task>? ShowWizard { get; set; }

    /// <summary>Opens a web page; set by the view.</summary>
    public Action<string>? OpenUrl { get; set; }

    /// <summary>Opens a file dialog for an OpenVPN profile; set by the view.</summary>
    public Func<Task<(string Name, string Text)?>>? PickProfile { get; set; }

    public RangeObservableCollection<FleetRowView> Rows { get; } = [];

    public bool MayConnect { get; }

    public bool MayInstall { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsReading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCameraOpen), nameof(IsPeersOpen))]
    public partial FleetPanel Panel { get; set; }

    public bool IsCameraOpen => Panel == FleetPanel.Camera;

    public bool IsPeersOpen => Panel == FleetPanel.Peers;

    /// <summary>The selected row of the table; a row opens its camera's panel.</summary>
    [ObservableProperty]
    public partial FleetRowView? SelectedRow { get; set; }

    [ObservableProperty]
    public partial CameraSettingsViewModel? Camera { get; private set; }

    [ObservableProperty]
    public partial string PeersText { get; private set; } = "";

    [ObservableProperty]
    public partial string? ErrorText { get; private set; }

    /// <summary>What the last wizard started.</summary>
    [ObservableProperty]
    public partial string? InfoText { get; private set; }

    /// <summary>"12 devices: 9 connected, 2 not connected, 1 without a VPN app. 3 updates".</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    /// <summary>Why the newest versions are not known; null when they are.</summary>
    [ObservableProperty]
    public partial string? ReleaseNote { get; private set; }

    public void Start() => _run ??= RunAsync();

    private Task RunAsync() => _ctx.RunAsync(
        async ct => Apply(await _ctx.InvokeAsync(Fleet.GetState, null, ct)),
        item =>
        {
            if (item.Topic == Fleet.StateTopic)
            {
                Apply(item.PayloadJson);
            }
        },
        new PluginEventWatchOptions { Failed = ex => ErrorText = ServerErrors.Message(ex) },
        _stop.Token);

    [RelayCommand]
    private async Task ChooseDevicesAsync()
    {
        if (PickDevices is { } pick && await pick() && CanRefresh())
        {
            await RefreshAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        ErrorText = null;
        var all = Devices.Choice.Kind == DeviceChoiceKind.All;
        var ids = all ? [] : Devices.Resolve().Select(d => d.Id).ToList();
        ErrorText = await GuardedRun.RunAsync(
            () => _ctx.InvokeAsync(Fleet.Refresh, JsonSerializer.Serialize(new RefreshRequest(all, ids), Fleet.Json), _stop.Token),
            GuardedCancel.Ignore);
    }

    private bool CanRefresh() => !IsReading && !Devices.IsEmpty;

    // ---- Panels ----

    partial void OnSelectedRowChanged(FleetRowView? value)
    {
        if (value is null)
        {
            return; // the table was reloaded: the open panel stays
        }

        Camera = new CameraSettingsViewModel(_ctx, value, MayConnect, MayInstall, _stop.Token)
        {
            OpenUrl = OpenUrl,
            PickProfile = PickProfile,
            StartInstall = InstallOneAsync,
        };
        Panel = FleetPanel.Camera;
        _ = Camera.LoadAsync();
    }

    [RelayCommand]
    private void ClosePanel() => Panel = FleetPanel.None;

    [RelayCommand]
    private async Task ShowPeersAsync()
    {
        Panel = Panel == FleetPanel.Peers ? FleetPanel.None : FleetPanel.Peers;
        if (Panel != FleetPanel.Peers)
        {
            return;
        }

        string? reply = null;
        ErrorText = await GuardedRun.RunAsync(async () => reply = await _ctx.InvokeAsync(Fleet.GetPeers, null, _stop.Token), GuardedCancel.Ignore);
        if (reply is not null && JsonSerializer.Deserialize<PeersReply>(reply, Fleet.Json) is { } peers)
        {
            PeersText = peers.Count == 0
                ? "No camera uses another WireGuard server yet. Set up cameras with WireGuard and \"Another WireGuard server\" first."
                : peers.Text;
        }
    }

    // ---- Wizards ----

    /// <summary>The install wizard: the app and its version, the cameras, what happens.</summary>
    public RolloutWizardViewModel InstallWizard()
    {
        var install = new InstallViewModel(_ctx, _stop.Token) { OpenUrl = OpenUrl };
        return new RolloutWizardViewModel(WizardMode.Install, install, null, (kind, _, latest) => InstallChecklist(kind, latest, install.FromFiles ? install.Builds : null), StartInstallAsync);
    }

    /// <summary>The set up wizard: the VPN and its settings, the cameras, what happens. A new form each time: no key stays.</summary>
    public RolloutWizardViewModel SetupWizard()
    {
        var form = new ConnectFormViewModel { NameOf = id => _ctx.Devices.FirstOrDefault(d => d.Id == id) is { } d ? DeviceLabel(d) : null };
        return new RolloutWizardViewModel(WizardMode.Setup, null, form, (kind, hub, _) => ConnectChecklist(kind, hub), StartSetupAsync)
        {
            HubCandidates = Devices.Candidates,
        };
    }

    [RelayCommand(CanExecute = nameof(MayInstall))]
    private async Task OpenInstallAsync()
    {
        var wizard = InstallWizard();
        _ = wizard.Install!.LoadReleasesAsync(checkNow: false);
        await RunWizardAsync(wizard);
    }

    [RelayCommand(CanExecute = nameof(MayConnect))]
    private Task OpenSetupAsync() => RunWizardAsync(SetupWizard());

    private async Task RunWizardAsync(RolloutWizardViewModel wizard)
    {
        if (ShowWizard is not { } show)
        {
            return;
        }

        InfoText = null;
        await show(wizard);
        if (wizard.Result is { } result)
        {
            InfoText = result;
        }
    }

    /// <summary>
    /// The chosen devices with what a set up does to each: the ones with the app are ticked, the ones without it are not
    /// (the task would refuse them), the hub is never in.
    /// </summary>
    public IReadOnlyList<CameraCheck> ConnectChecklist(VpnKind kind, Guid? hubId)
    {
        var app = VpnApps.Of(kind);
        var list = new List<CameraCheck>();
        foreach (var device in Devices.Resolve())
        {
            CameraCheck Check(string installed, string note, CheckKind what, bool on) => new(device.Id, device.Address, device.Model ?? "", installed, note, what, on);
            if (device.Id == hubId)
            {
                list.Add(Check("", "Hub camera: gets the others as peers.", CheckKind.Hub, false));
            }
            else if (!_rowsByDevice.TryGetValue(device.Id, out var rows))
            {
                list.Add(Check("", "Not read yet. Refresh to check it has the app.", CheckKind.NotRead, true));
            }
            else if (rows.FirstOrDefault(r => r.Vpn == kind) is { } row)
            {
                list.Add(Check(row.AppVersion ?? "", $"{app.DisplayName} installed", CheckKind.Ready, true));
            }
            else if (rows.FirstOrDefault(r => r.Error is not null) is { } failed)
            {
                list.Add(Check("", "Not read: " + failed.Error, CheckKind.Failed, false));
            }
            else
            {
                list.Add(Check("", $"{app.DisplayName} not installed. Install it first.", CheckKind.NoApp, false));
            }
        }

        return list;
    }

    /// <summary>
    /// The chosen devices with what the install does to each: outdated ones are ticked, the others not. With packages from
    /// this computer (<paramref name="builds"/>), a camera that none of them fits is not ticked.
    /// </summary>
    public IReadOnlyList<CameraCheck> InstallChecklist(VpnKind kind, string? latest, IReadOnlyList<AcapBuild>? builds = null)
    {
        var app = VpnApps.Of(kind);
        var list = new List<CameraCheck>();
        foreach (var device in Devices.Resolve())
        {
            CameraCheck Check(string installed, string note, CheckKind what, bool on) => new(device.Id, device.Address, device.Model ?? "", installed, note, what, on);
            var installed = _rowsByDevice.TryGetValue(device.Id, out var known) ? known.FirstOrDefault(r => r.Vpn == kind)?.AppVersion ?? "" : "";
            if (builds is not null && NoPackageFits(builds, device) is { } why)
            {
                list.Add(Check(installed, why, CheckKind.NoPackage, false));
            }
            else if (!_rowsByDevice.TryGetValue(device.Id, out var rows))
            {
                list.Add(Check("", "Not read yet", CheckKind.NotRead, false));
            }
            else if (rows.FirstOrDefault(r => r.Vpn == kind)?.AppVersion is { } have)
            {
                list.Add(latest is not null && AppVersions.IsNewer(latest, have)
                    ? Check(have, $"Upgrade {have} to {latest}", CheckKind.Upgrade, true)
                    : Check(have, $"{have}, the newest", CheckKind.UpToDate, false));
            }
            else if (rows.FirstOrDefault(r => r.Error is not null) is { } failed)
            {
                list.Add(Check("", "Not read: " + failed.Error, CheckKind.Failed, false));
            }
            else
            {
                list.Add(Check("", latest is null ? "Install" : $"Install {app.DisplayName} {latest}", CheckKind.Install, false));
            }
        }

        return list;
    }

    /// <summary>Why none of the packages fits the device ("No package for armv7hf"), or null; unknown devices are checked by the task.</summary>
    private static string? NoPackageFits(IReadOnlyList<AcapBuild> builds, Oadm.Sdk.Devices.IDeviceInfo device)
    {
        var group = AcapDeviceGroup.Of(device);
        if (!group.IsKnown)
        {
            return null;
        }

        var choice = AcapBuildChooser.Choose(builds, group);
        if (choice.Pick != AcapPick.Chosen || choice.Index < 0)
        {
            return "No package fits: " + (choice.Reason ?? $"none for {group.Architecture}");
        }

        return AcapBuildChooser.Misfit(builds[choice.Index].Package, group) is { } why ? $"No package fits: {builds[choice.Index].Name}, {why}" : null;
    }

    private async Task<(string? Info, string? Error)> StartInstallAsync(RolloutWizardViewModel wizard)
    {
        var install = wizard.Install!;
        IReadOnlyList<PackageFile>? files = null;
        if (install.FromFiles)
        {
            var sent = new List<PackageFile>();
            foreach (var package in install.Packages.ToList())
            {
                var name = package.Name;
                var progress = new Progress<double>(f => wizard.StartingText = $"Uploading {name}, {f:P0}");
                wizard.StartingText = $"Uploading {name}";
                string? problem = null;
                UploadedFile? uploaded = null;
                problem = await GuardedRun.RunAsync(async () => uploaded = await _ctx.UploadAsync(package.Path, progress, _stop.Token), GuardedCancel.Ignore);
                if (problem is not null || uploaded is null)
                {
                    return (null, $"{name} was not uploaded: {problem ?? "the upload stopped."}");
                }

                sent.Add(new PackageFile(uploaded.Id, name));
            }

            files = sent;
            wizard.StartingText = "Starting the tasks";
        }

        return await InstallAsync(install.SelectedApp.Kind, wizard.Cameras.CheckedIds, wizard.Cameras.All.Where(c => c.IsChecked).All(c => c.HasApp), files);
    }

    private async Task<string?> InstallOneAsync(VpnKind kind, IReadOnlyList<Guid> deviceIds, bool upgrade)
    {
        var (info, error) = await InstallAsync(kind, deviceIds, upgrade);
        ErrorText = error;
        return info;
    }

    /// <summary>Starts install or upgrade tasks: the text for the page, or the problem.</summary>
    private async Task<(string? Info, string? Error)> InstallAsync(VpnKind kind, IReadOnlyList<Guid> deviceIds, bool upgrade, IReadOnlyList<PackageFile>? files = null)
    {
        if (!MayInstall)
        {
            return (null, "Your role cannot install or upgrade VPN apps.");
        }

        string? reply = null;
        var error = await GuardedRun.RunAsync(
            async () => reply = await _ctx.InvokeAsync(Fleet.Install, JsonSerializer.Serialize(new InstallRequest(kind, deviceIds, upgrade) { Files = files }, Fleet.Json), _stop.Token),
            GuardedCancel.Ignore);
        if (error is not null || reply is null || JsonSerializer.Deserialize<InstallReply>(reply, Fleet.Json) is not { } result)
        {
            return (null, error ?? "The tasks did not start.");
        }

        var what = upgrade ? $"Upgrading {VpnApps.NameOf(kind)} to {result.Version}" : $"Installing {VpnApps.NameOf(kind)} {result.Version}";
        return ($"{what} on {(result.Devices == 1 ? "1 camera" : $"{result.Devices:N0} cameras")}. Follow the tasks in the task list, then refresh this page.", null);
    }

    /// <summary>Starts the set up tasks; the keys are cleared from the form once they were sent.</summary>
    private async Task<(string? Info, string? Error)> StartSetupAsync(RolloutWizardViewModel wizard)
    {
        var form = wizard.Form!;
        string? reply = null;
        var error = await GuardedRun.RunAsync(
            async () => reply = await _ctx.InvokeAsync(Fleet.Connect, JsonSerializer.Serialize(form.ToRequest(false, wizard.Cameras.CheckedIds), Fleet.Json), _stop.Token),
            GuardedCancel.Ignore);
        if (error is not null || reply is null || JsonSerializer.Deserialize<ConnectReply>(reply, Fleet.Json) is not { } result)
        {
            return (null, error ?? "The tasks did not start.");
        }

        form.ClearSecrets();
        return ((result.Devices == 1 ? "Setting up 1 camera" : $"Setting up {result.Devices:N0} cameras")
            + $" with {VpnApps.NameOf(form.Kind)}. Follow the tasks in the task list, then refresh this page.", null);
    }

    // ---- State ----

    /// <summary>Shows a state from the server, unless an equal or newer one is shown already.</summary>
    public void Apply(string? json)
    {
        if (json is null || JsonSerializer.Deserialize<FleetState>(json, Fleet.Json) is not { } state || state.Version <= _version)
        {
            return;
        }

        _version = state.Version;
        IsReading = state.Reading;
        _rowsByDevice = state.Rows.GroupBy(r => r.DeviceId).ToDictionary(g => g.Key, g => g.ToList());
        Rows.ReplaceAll(state.Rows.Select(r => new FleetRowView(r)));
        Summary = SummaryOf(state.Rows, Devices.IsEmpty);
        ReleaseNote = state.ReleaseError is null ? null : "Newest versions unknown: " + state.ReleaseError;
    }

    public static string SummaryOf(IReadOnlyList<FleetRow> rows, bool nothingChosen)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return nothingChosen ? "Choose devices to see their VPN." : "Click Refresh to read the devices.";
        }

        var devices = rows.Select(r => r.DeviceId).Distinct().Count();
        var parts = new List<string>();
        void Count(int n, string text)
        {
            if (n > 0)
            {
                parts.Add($"{n:N0} {text}");
            }
        }

        Count(rows.Count(r => r.Error is null && r.State == VpnState.Connected), "connected");
        Count(rows.Count(r => r.Error is null && r.State is VpnState.NotConnected or VpnState.Connecting or VpnState.NotSetUp or VpnState.AppStopped or VpnState.Unknown), "not connected");
        Count(rows.Count(r => r.Error is null && r.State == VpnState.NoVpnApp), "without a VPN app");
        Count(rows.Count(r => r.Error is not null), "not read");
        var updates = rows.Count(r => r.UpdateAvailable);
        return (devices == 1 ? "1 device" : $"{devices:N0} devices") + (parts.Count > 0 ? ": " + string.Join(", ", parts) : "")
            + (updates == 0 ? "" : updates == 1 ? ". 1 update" : $". {updates:N0} updates");
    }

    private static string DeviceLabel(IDeviceInfo device) => string.IsNullOrEmpty(device.Model) ? device.Address : $"{device.Address} ({device.Model})";

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
