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
/// or upgrade and Set up many cameras open a wizard with the cameras in a table. Reads the table from the server part in
/// pages, then follows its events: the changed rows of some devices, applied in place in one batch per burst (read off
/// the UI thread). Nothing is read before a device choice.
/// </summary>
public sealed partial class VpnFleetViewModel : ObservableObject, IDisposable
{
    private readonly ICorePluginClientContext _ctx;
    private readonly CancellationTokenSource _stop = new();
    private readonly FleetTable _table = new();
    private readonly List<PluginEvent> _pending = [];
    private bool _draining;
    private int _generation;
    private int _stateVersion = int.MinValue;
    private bool _restoring;
    private Task? _run;

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

    /// <summary>Opens a web address in the browser; set by the view. <see cref="OpenUrl"/> decides what may be opened.</summary>
    public Func<Uri, Task>? Launch { get; set; }

    /// <summary>Opens a file dialog for an OpenVPN profile; set by the view.</summary>
    public Func<Task<ProfileFile?>>? PickProfile { get; set; }

    /// <summary>The rows the search shows; bound to the grid.</summary>
    public RangeObservableCollection<FleetRowView> Rows => _table.Shown;

    /// <summary>The page's copy of the server's table.</summary>
    public FleetTable Table => _table;

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

    /// <summary>Search over address, model, app, status, VPN address and details.</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

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
        ReadStateAsync,
        HandleEvent,
        new PluginEventWatchOptions { Failed = ex => ErrorText = ServerErrors.Message(ex) },
        _stop.Token);

    /// <summary>Opens a release page of the apps; nothing but GitHub.</summary>
    public void OpenUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && GitHubHttp.IsGitHub(uri) && Launch is { } launch)
        {
            _ = launch(uri);
        }
    }

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

    partial void OnSearchTextChanged(string value) => KeepSelection(() => _table.Filter(value));

    // ---- Panels ----

    partial void OnSelectedRowChanged(FleetRowView? value)
    {
        if (value is null || _restoring)
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
            else if (!_table.TryGetRows(device.Id, out var rows))
            {
                list.Add(Check("", "Not read yet. Refresh to check it has the app.", CheckKind.NotRead, true));
            }
            else if (rows.FirstOrDefault(r => r.Kind == kind)?.Row is { } row)
            {
                list.Add(Check(row.AppVersion ?? "", $"{app.DisplayName} installed", CheckKind.Ready, true));
            }
            else if (rows.FirstOrDefault(r => r.Row.Error is not null)?.Row is { } failed)
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
            var installed = _table.TryGetRows(device.Id, out var known) ? known.FirstOrDefault(r => r.Kind == kind)?.Row.AppVersion ?? "" : "";
            if (builds is not null && NoPackageFits(builds, device) is { } why)
            {
                list.Add(Check(installed, why, CheckKind.NoPackage, false));
            }
            else if (!_table.TryGetRows(device.Id, out var rows))
            {
                list.Add(Check("", "Not read yet", CheckKind.NotRead, false));
            }
            else if (rows.FirstOrDefault(r => r.Kind == kind)?.Row.AppVersion is { } have)
            {
                list.Add(latest is not null && AppVersions.IsNewer(latest, have)
                    ? Check(have, $"Upgrade {have} to {latest}", CheckKind.Upgrade, true)
                    : Check(have, $"{have}, the newest", CheckKind.UpToDate, false));
            }
            else if (rows.FirstOrDefault(r => r.Row.Error is not null)?.Row is { } failed)
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

    /// <summary>
    /// Reads the whole table in pages (each page read off the UI thread) and shows it; events of an earlier connection
    /// that still wait are dropped. Called at the start and after every reconnect.
    /// </summary>
    public async Task ReadStateAsync(CancellationToken ct)
    {
        lock (_pending)
        {
            _pending.Clear();
            _generation++;
        }

        var pages = new List<FleetState>();
        Guid? after = null;
        do
        {
            var json = await _ctx.InvokeAsync(Fleet.GetState, JsonSerializer.Serialize(new StatePageRequest(after, Fleet.StatePageSize), Fleet.Json), ct);
            var page = await Task.Run(() => Parse<FleetState>(json), ct).ConfigureAwait(true);
            pages.Add(page);
            after = page.NextAfter;
        }
        while (after is not null);

        ApplySnapshot(pages);
    }

    /// <summary>A whole table (tests, and the pages of a read): replaces every row, keeps the selection.</summary>
    public void Apply(string? json)
    {
        if (json is not null && JsonSerializer.Deserialize<FleetState>(json, Fleet.Json) is { } state)
        {
            ApplySnapshot([state]);
        }
    }

    private void ApplySnapshot(IReadOnlyList<FleetState> pages)
    {
        KeepSelection(() => _table.Replace(pages));
        _stateVersion = int.MinValue; // a read is the newest truth, also from a restarted server
        ApplyState(pages[^1]);
        UpdateSummary();
    }

    /// <summary>
    /// One event of the server (on the UI thread): queued; the events of a burst are read off the UI thread and applied
    /// together, with one reset of the table at most.
    /// </summary>
    public void HandleEvent(PluginEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Topic is not (Fleet.RowsTopic or Fleet.StateTopic) || item.PayloadJson is null)
        {
            return;
        }

        lock (_pending)
        {
            _pending.Add(item);
            if (_draining)
            {
                return;
            }

            _draining = true;
        }

        _ = DrainAsync();
    }

    /// <summary>True while events wait or are applied (tests wait for it to end).</summary>
    public bool IsApplying
    {
        get
        {
            lock (_pending)
            {
                return _draining;
            }
        }
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            List<PluginEvent> batch;
            int generation;
            lock (_pending)
            {
                if (_pending.Count == 0)
                {
                    _draining = false;
                    return;
                }

                batch = [.. _pending];
                _pending.Clear();
                generation = _generation;
            }

            try
            {
                var parsed = await Task.Run(() => ParseBatch(batch)).ConfigureAwait(true);
                if (generation == _generation)
                {
                    ApplyBatch(parsed.Rows, parsed.States);
                }
            }
#pragma warning disable CA1031 // A broken event is shown; the next read brings the table back.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                ErrorText = ServerErrors.Message(ex);
            }
        }
    }

    private static (List<FleetRowsEvent> Rows, List<FleetState> States) ParseBatch(List<PluginEvent> batch)
    {
        var rows = new List<FleetRowsEvent>();
        var states = new List<FleetState>();
        foreach (var item in batch)
        {
            if (item.Topic == Fleet.RowsTopic)
            {
                rows.Add(Parse<FleetRowsEvent>(item.PayloadJson));
            }
            else
            {
                states.Add(Parse<FleetState>(item.PayloadJson));
            }
        }

        return (rows, states);
    }

    /// <summary>The events of a burst: an emptied table first, then the rows in place, then the newest state.</summary>
    public void ApplyBatch(IReadOnlyList<FleetRowsEvent> rows, IReadOnlyList<FleetState> states)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(states);
        KeepSelection(() =>
        {
            foreach (var cleared in states.Where(s => s.Cleared))
            {
                _table.Clear(cleared.Version);
            }

            _table.Apply(rows);
        });
        if (states.Count > 0)
        {
            ApplyState(states.MaxBy(s => s.Version)!);
        }

        UpdateSummary();
    }

    private void ApplyState(FleetState state)
    {
        if (state.Version < _stateVersion && !state.Cleared)
        {
            return;
        }

        _stateVersion = state.Version;
        IsReading = state.Reading;
        ReleaseNote = state.ReleaseError is null ? null : "Newest versions unknown: " + state.ReleaseError;
    }

    private void UpdateSummary() => Summary = _table.Counts.Text(Devices.IsEmpty);

    /// <summary>A reset of the grid drops its selection: the selected row is chosen again when it is still there.</summary>
    private void KeepSelection(Action change)
    {
        var selected = SelectedRow;
        change();
        if (selected is not null && _table.Contains(selected) && Rows.Count > 0)
        {
            _restoring = true;
            try
            {
                SelectedRow = null;
                SelectedRow = selected;
            }
            finally
            {
                _restoring = false;
            }
        }
    }

    private static T Parse<T>(string? json) =>
        JsonSerializer.Deserialize<T>(json ?? "null", Fleet.Json) ?? throw new JsonException("The server sent no " + typeof(T).Name + ".");

    public static string SummaryOf(IReadOnlyList<FleetRow> rows, bool nothingChosen) => FleetCounts.Of(rows).Text(nothingChosen);

    private static string DeviceLabel(IDeviceInfo device) => string.IsNullOrEmpty(device.Model) ? device.Address : $"{device.Address} ({device.Model})";

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
