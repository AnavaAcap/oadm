using System.Globalization;
using System.Text.Json;

using Oadm.Sdk.Devices;
using Oadm.Plugins.VpnFleet.Tools;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.VpnFleet;

/// <summary>A camera's WireGuard tunnel as the plugin remembers it: never its private key.</summary>
public sealed record WireGuardRecord(Guid DeviceId, string PeerName, string PublicKey, string Address, string Network, Guid? HubDeviceId);

/// <summary>
/// The server part of VPN Fleet. The page reads the VPN apps of the chosen devices (a job per read, one table per
/// device group) and connects devices to a VPN (one task per device). For WireGuard it makes each camera's keys,
/// gives it a tunnel address and, with a WireGuard Server camera as hub, adds the cameras to the hub's peers.
/// </summary>
public sealed class VpnFleetPlugin : ICorePlugin, IDisposable
{
    public const string WireGuardSettingKey = "wireguard";

    private const int ReadParallelism = 16;
    private static readonly TimeSpan DeviceTimeout = TimeSpan.FromSeconds(15);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, FleetTable> _tables = []; // by caller.DeviceScopeKey
    private readonly SemaphoreSlim _wireGuardGate = new(1, 1);
    private readonly ConnectTaskPlugin _connectTask;
    private readonly InstallTaskPlugin _installTask;
    private readonly ReleaseCatalog _catalog;
    private ICorePluginContext? _ctx;
    private int _version;

    public VpnFleetPlugin()
        : this(TimeProvider.System, GitHubHttp.CreateHandler())
    {
    }

    internal VpnFleetPlugin(TimeProvider time, HttpMessageHandler github)
    {
        _connectTask = new ConnectTaskPlugin(time);
        _catalog = new ReleaseCatalog(github, time, PackageFolder);
        _installTask = new InstallTaskPlugin(() => _catalog);
    }

    internal ConnectTaskPlugin ConnectTask => _connectTask;

    internal InstallTaskPlugin InstallTask => _installTask;

    /// <summary>Where the downloaded packages are kept: the plugin's own folder of the server.</summary>
    private string PackageFolder() =>
        Path.Combine(_ctx?.DataDirectory ?? Path.Combine(Path.GetTempPath(), Fleet.PluginId), "packages");

    public string Id => Fleet.PluginId;

    public string DisplayName => "VPN Fleet";

    public string? IconKey => "globeLock";

    public CorePluginGroup Group => CorePluginGroup.Security;

    public IReadOnlyList<ITaskPlugin> TaskPlugins => [_connectTask, _installTask];

    public IReadOnlyList<PermissionDefinition> Permissions =>
    [
        new(Fleet.ConnectPermission, "Change VPN settings", "Change the settings and keys of the VPN apps on devices.") { OperatorDefault = false },
        new(Fleet.InstallPermission, "Install and upgrade VPN apps", "Install the VPN apps from GitHub on devices and upgrade them.") { OperatorDefault = false },
    ];

    public IReadOnlyList<PluginTool> Tools => VpnFleetTools.Set.Tools;

    public string? RequiredPermission(string method) => VpnFleetTools.Set.RulesMethodOf(method) switch
    {
        Fleet.Connect or Fleet.SaveSettings or Fleet.NewKeyPair => Fleet.ConnectPermission,
        Fleet.Install => Fleet.InstallPermission,
        _ => PermissionKeys.Show,
    };

    public bool IsAudited(string method) =>
        !PluginTool.IsPreviewMethod(method) && VpnFleetTools.Set.RulesMethodOf(method) is Fleet.Connect or Fleet.SaveSettings or Fleet.NewKeyPair or Fleet.Install;

    public string? AuditDetail(string method, string? payloadJson)
    {
        try
        {
            if (VpnFleetTools.Set.Handles(method))
            {
                return null; // the host's audit names the tool
            }

            switch (method)
            {
                case Fleet.Connect:
                    var request = JsonSerializer.Deserialize<ConnectRequest>(payloadJson ?? "null", Fleet.Json);
                    return request is null
                        ? null
                        : $"Connect {(request.All ? "all" : request.DeviceIds.Count.ToString(CultureInfo.InvariantCulture))} devices to {VpnApps.NameOf(request.Kind)}";
                case Fleet.SaveSettings:
                    var save = JsonSerializer.Deserialize<SaveSettingsRequest>(payloadJson ?? "null", Fleet.Json);
                    return save is null ? null : $"Change {VpnApps.NameOf(save.Kind)} settings: {string.Join(", ", save.Values.Keys)}";
                case Fleet.Install:
                    var install = JsonSerializer.Deserialize<InstallRequest>(payloadJson ?? "null", Fleet.Json);
                    return install is null ? null : $"{(install.Upgrade ? "Upgrade" : "Install")} {VpnApps.NameOf(install.Kind)} on {install.DeviceIds.Count} devices";
                case Fleet.NewKeyPair:
                    return "New WireGuard key pair";
                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
    {
        _ctx = ctx;
        if (ctx.DeviceScopes is { } scopes)
        {
            scopes.Changed += OnGroupChanged;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        if (_ctx?.DeviceScopes is { } scopes)
        {
            scopes.Changed -= OnGroupChanged;
        }

        return Task.CompletedTask;
    }

    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
        throw new NotSupportedException("The page needs the user's call.");

    public async Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (VpnFleetTools.Set.Handles(method))
        {
            return await VpnFleetTools.Set.InvokeAsync(this, caller, method, payloadJson, ct).ConfigureAwait(false);
        }

        var scope = caller.DeviceScopeKey;
        switch (method)
        {
            case Fleet.GetState:
                var page = payloadJson is null ? null : JsonSerializer.Deserialize<StatePageRequest>(payloadJson, Fleet.Json);
                return Serialize(PageOf(scope, page?.After, page?.Limit ?? Fleet.StatePageSize));

            case Fleet.Refresh:
                var request = JsonSerializer.Deserialize<RefreshRequest>(payloadJson ?? "null", Fleet.Json);
                if (request is null || (!request.All && (request.DeviceIds is null || request.DeviceIds.Count == 0)))
                {
                    throw new ArgumentException(Fleet.NothingChosen);
                }

                StartRead(caller, request.All ? await caller.Devices.ListAsync(ct).ConfigureAwait(false) : await FindAsync(caller, request.DeviceIds, ct).ConfigureAwait(false));
                return null;

            case Fleet.Connect:
                var connect = JsonSerializer.Deserialize<ConnectRequest>(payloadJson ?? "null", Fleet.Json)
                    ?? throw new ArgumentException("The request has no settings.");
                return JsonSerializer.Serialize(await ConnectAsync(caller, connect, ct).ConfigureAwait(false), Fleet.Json);

            case Fleet.GetPeers:
                return JsonSerializer.Serialize(await PeersAsync(caller, ct).ConfigureAwait(false), Fleet.Json);

            case Fleet.GetReleases:
                var releases = JsonSerializer.Deserialize<ReleasesRequest>(payloadJson ?? "null", Fleet.Json);
                return JsonSerializer.Serialize(await _catalog.GetAllAsync(releases?.CheckNow ?? false, ct).ConfigureAwait(false), Fleet.Json);

            case Fleet.Install:
                var install = JsonSerializer.Deserialize<InstallRequest>(payloadJson ?? "null", Fleet.Json)
                    ?? throw new ArgumentException("The request has no app.");
                return JsonSerializer.Serialize(await InstallAsync(caller, install, ct).ConfigureAwait(false), Fleet.Json);

            case Fleet.GetSettings:
                var read = JsonSerializer.Deserialize<SettingsRequest>(payloadJson ?? "null", Fleet.Json)
                    ?? throw new ArgumentException("The request has no device.");
                return JsonSerializer.Serialize(await GetSettingsAsync(caller, read, ct).ConfigureAwait(false), Fleet.Json);

            case Fleet.SaveSettings:
                var save = JsonSerializer.Deserialize<SaveSettingsRequest>(payloadJson ?? "null", Fleet.Json)
                    ?? throw new ArgumentException("The request has no device.");
                return JsonSerializer.Serialize(await SaveSettingsAsync(caller, save, ct).ConfigureAwait(false), Fleet.Json);

            case Fleet.NewKeyPair:
                var keyPair = JsonSerializer.Deserialize<NewKeyPairRequest>(payloadJson ?? "null", Fleet.Json)
                    ?? throw new ArgumentException("The request has no device.");
                return JsonSerializer.Serialize(await NewKeyPairAsync(caller, keyPair, ct).ConfigureAwait(false), Fleet.Json);

            default:
                throw new ArgumentException("Unknown method " + method);
        }
    }

    /// <summary>The whole table of the caller's device group, sorted like the page (the tools).</summary>
    internal FleetState StateFor(ICallContext caller)
    {
        var scope = caller.DeviceScopeKey;
        lock (_gate)
        {
            var table = TableOf(scope);
            return new FleetState(_version, table.Reading,
                [.. table.Rows.Where(d => InGroup(scope, d.Key)).SelectMany(d => d.Value).OrderBy(r => r.Address, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Vpn)])
            {
                ReleaseError = table.ReleaseError,
            };
        }
    }

    internal ReleaseCatalog Catalog => _catalog;

    /// <summary>Reads the devices in a job of the caller; one read per device group at a time.</summary>
    internal void StartRead(ICallContext caller, IReadOnlyList<IDeviceInfo> devices)
    {
        var scope = caller.DeviceScopeKey;
        if (!TryBeginRead(scope))
        {
            throw new InvalidOperationException("The devices are being read already.");
        }

        try
        {
            _ctx!.Jobs!.Start(caller, "read", (job, token) => ReadAsync(job.Owner, scope, devices, token));
        }
        catch
        {
            EndRead(scope, null, keepReleaseError: true);
            throw;
        }
    }

    internal static async Task<List<IDeviceInfo>> FindAsync(ICallContext caller, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var devices = new List<IDeviceInfo>(ids.Count);
        foreach (var id in ids.Distinct())
        {
            devices.Add(await caller.Devices.FindAsync(id, ct).ConfigureAwait(false) ?? throw new DevicesNotAvailableException());
        }

        return devices;
    }

    // ---- Connect ----

    private async Task<ConnectReply> ConnectAsync(ICallContext caller, ConnectRequest request, CancellationToken ct)
    {
        if (ConnectRules.Validate(request) is { Count: > 0 } problems)
        {
            throw new ArgumentException(problems[0].Message);
        }

        if (!request.All && request.DeviceIds.Count == 0)
        {
            throw new ArgumentException(Fleet.NothingChosen);
        }

        IReadOnlyList<IDeviceInfo> devices = request.All
            ? await caller.Devices.ListAsync(ct).ConfigureAwait(false)
            : await FindAsync(caller, request.DeviceIds, ct).ConfigureAwait(false);
        devices = [.. devices.Where(d => d.Id != request.HubDeviceId)];
        if (devices.Count == 0)
        {
            throw new ArgumentException("Choose devices other than the hub.");
        }

        var settings = request with { DeviceIds = [] };
        if (request.Kind != VpnKind.WireGuard)
        {
            var ids = await caller.Tasks.RunAsync(ConnectTaskPlugin.PluginId, [.. devices.Select(d => d.Id)],
                JsonSerializer.Serialize(new ConnectPayload { Settings = settings }, Fleet.Json), caller.UserName, ct).ConfigureAwait(false);
            return new ConnectReply(devices.Count, ids);
        }

        return await ConnectWireGuardAsync(caller, settings, devices, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Makes a key pair and a tunnel address for every camera, remembers them (public keys only), adds the cameras to the
    /// hub when there is one, then starts the cameras' tasks. One run at a time, so two runs never hand out one address.
    /// </summary>
    private async Task<ConnectReply> ConnectWireGuardAsync(ICallContext caller, ConnectRequest settings, IReadOnlyList<IDeviceInfo> devices, CancellationToken ct)
    {
        await _wireGuardGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var records = await LoadRecordsAsync(ct).ConfigureAwait(false);
            IDeviceInfo? hub = null;
            AddressPool pool;
            string endpoint, serverKey;
            var reserved = new HashSet<uint>();
            if (settings.HubDeviceId is { } hubId)
            {
                hub = await caller.Devices.FindAsync(hubId, ct).ConfigureAwait(false) ?? throw new DevicesNotAvailableException();
                if (DeviceMessages.Refusal(hub, refuseUnreachable: true) is { } refusal)
                {
                    throw new InvalidOperationException("The hub camera cannot be used: " + refusal);
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(DeviceTimeout);
                var vapix = await caller.Vapix.CreateAsync(hub.Id, timeout.Token).ConfigureAwait(false);
                var hubInfo = await DeviceVpn.ReadHubAsync(vapix, hub.Address, timeout.Token).ConfigureAwait(false);
                pool = hubInfo.Pool;
                endpoint = hubInfo.Endpoint;
                serverKey = hubInfo.PublicKey;
                reserved.Add(pool.Address);

                // The hub's other peers keep their addresses; the cameras of this run get theirs back.
                var names = devices.Select(d => WireGuardAddressing.PeerName(d.Serial, d.Address)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var peer in WireGuardAddressing.ParsePeers(hubInfo.Peers).Where(p => !names.Contains(p.Name)))
                {
                    if (WireGuardAddressing.ParseAddress(peer.Address) is { } used)
                    {
                        reserved.Add(used);
                    }
                }
            }
            else
            {
                pool = WireGuardAddressing.ParsePool(settings.AddressPool)!;
                endpoint = settings.Endpoint!.Trim();
                serverKey = settings.ServerPublicKey!.Trim();
                reserved.Add(pool.IsHost(pool.Address) ? pool.Address : pool.Network + 1); // the server's own address
            }

            var runIds = devices.Select(d => d.Id).ToHashSet();
            foreach (var other in records.Where(r => !runIds.Contains(r.DeviceId) && r.Network == pool.Subnet && r.HubDeviceId == settings.HubDeviceId))
            {
                if (WireGuardAddressing.ParseAddress(other.Address) is { } used)
                {
                    reserved.Add(used);
                }
            }

            var previous = records
                .Where(r => runIds.Contains(r.DeviceId) && r.Network == pool.Subnet)
                .Select(r => (r.DeviceId, Address: WireGuardAddressing.ParseAddress(r.Address)))
                .Where(r => r.Address is not null)
                .ToDictionary(r => r.DeviceId, r => r.Address!.Value);
            var addresses = WireGuardAddressing.Allocate(pool, [.. devices.Select(d => d.Id)], previous, reserved);

            var cameras = new Dictionary<Guid, WireGuardCamera>();
            var peers = new List<HubPeer>();
            var updated = records.Where(r => !runIds.Contains(r.DeviceId)).ToList();
            foreach (var device in devices)
            {
                var keys = WireGuardKeys.Generate();
                var address = WireGuardAddressing.Format(addresses[device.Id]);
                var name = WireGuardAddressing.PeerName(device.Serial, device.Address);
                cameras[device.Id] = new WireGuardCamera(keys.PrivateKey, pool.WithPrefix(addresses[device.Id]));
                peers.Add(new HubPeer(name, keys.PublicKey, address));
                updated.Add(new WireGuardRecord(device.Id, name, keys.PublicKey, address, pool.Subnet, settings.HubDeviceId));
            }

            var allowed = WireGuardAddressing.ParseNetworks(settings.AllowedIps) is { Count: > 0 } networks ? string.Join(",", networks) : pool.Subnet;
            var taskIds = new List<Guid>();
            if (hub is not null)
            {
                taskIds.AddRange(await caller.Tasks.RunAsync(ConnectTaskPlugin.PluginId, [hub.Id],
                    JsonSerializer.Serialize(new ConnectPayload { Settings = settings, HubPeers = peers }, Fleet.Json), caller.UserName, ct).ConfigureAwait(false));
            }

            await _ctx!.Settings.SetAsync(WireGuardSettingKey, JsonSerializer.Serialize(updated, Fleet.Json), ct).ConfigureAwait(false);
            var payload = new ConnectPayload
            {
                Settings = settings,
                Cameras = cameras,
                Endpoint = endpoint,
                PeerPublicKey = serverKey,
                AllowedIps = allowed,
            };
            taskIds.AddRange(await caller.Tasks.RunAsync(ConnectTaskPlugin.PluginId, [.. devices.Select(d => d.Id)],
                JsonSerializer.Serialize(payload, Fleet.Json), caller.UserName, ct).ConfigureAwait(false));
            return new ConnectReply(devices.Count, taskIds);
        }
        finally
        {
            _wireGuardGate.Release();
        }
    }

    /// <summary>The [Peer] sections of the user's cameras that use another WireGuard server than a hub camera.</summary>
    private async Task<PeersReply> PeersAsync(ICallContext caller, CancellationToken ct)
    {
        var sections = new List<string>();
        foreach (var record in (await LoadRecordsAsync(ct).ConfigureAwait(false)).Where(r => r.HubDeviceId is null))
        {
            if (await caller.Devices.FindAsync(record.DeviceId, ct).ConfigureAwait(false) is { } device)
            {
                sections.Add(WireGuardAddressing.PeerSection($"{device.Address} {device.Model} ({record.PeerName})", record.PublicKey, record.Address));
            }
        }

        return new PeersReply(sections.Count, string.Join("\n", sections));
    }

    private async Task<List<WireGuardRecord>> LoadRecordsAsync(CancellationToken ct)
    {
        var json = await _ctx!.Settings.GetAsync(WireGuardSettingKey, ct).ConfigureAwait(false);
        return json is null ? [] : JsonSerializer.Deserialize<List<WireGuardRecord>>(json, Fleet.Json) ?? [];
    }

    // ---- Install and upgrade ----

    private async Task<InstallReply> InstallAsync(ICallContext caller, InstallRequest request, CancellationToken ct)
    {
        var (devices, version) = await PlanInstallAsync(caller, request.Kind, request.DeviceIds, request.Files, ct).ConfigureAwait(false);
        return await RunInstallAsync(caller, request.Kind, devices, version, request.Upgrade, request.Files, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The devices (in the caller's view) and the version an install would take; nothing is changed. With
    /// <paramref name="files"/> the packages are the caller's own uploads and GitHub is never asked.
    /// </summary>
    internal async Task<(IReadOnlyList<IDeviceInfo> Devices, string Version)> PlanInstallAsync(
        ICallContext caller, VpnKind kind, IReadOnlyList<Guid>? deviceIds, IReadOnlyList<PackageFile>? files, CancellationToken ct)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentException("Choose an app.");
        }

        if (deviceIds is not { Count: > 0 })
        {
            throw new ArgumentException(Fleet.NothingChosen);
        }

        var devices = await FindAsync(caller, deviceIds, ct).ConfigureAwait(false);
        if (files is { Count: > 0 })
        {
            var builds = await LocalPackages.ReadAsync(caller.Uploads, VpnApps.Of(kind), files, ct).ConfigureAwait(false);
            return (devices, builds[0].Package.Version);
        }

        var (release, error) = await _catalog.GetAsync(kind, checkNow: false, ct).ConfigureAwait(false);
        return release is null
            ? throw new InvalidOperationException(error ?? "The newest version could not be read.")
            : (devices, release.Version);
    }

    internal static async Task<InstallReply> RunInstallAsync(
        ICallContext caller, VpnKind kind, IReadOnlyList<IDeviceInfo> devices, string version, bool upgrade, IReadOnlyList<PackageFile>? files, CancellationToken ct)
    {
        var payload = new InstallPayload(kind, version, upgrade) { Files = files };
        var ids = await caller.Tasks.RunAsync(InstallTaskPlugin.PluginId, [.. devices.Select(d => d.Id)],
            JsonSerializer.Serialize(payload, Fleet.Json), caller.UserName, ct).ConfigureAwait(false);
        return new InstallReply(devices.Count, version, ids);
    }

    // ---- Settings of one camera ----

    private static async Task<(IDeviceInfo Device, Oadm.Sdk.Vapix.IVapixClient Vapix, IReadOnlyDictionary<string, string> Parameters)> OpenAppAsync(
        ICallContext caller, Guid deviceId, VpnApp app, CancellationToken ct)
    {
        var device = await caller.Devices.FindAsync(deviceId, ct).ConfigureAwait(false) ?? throw new DevicesNotAvailableException();
        if (DeviceMessages.Refusal(device, refuseUnreachable: true) is { } refusal)
        {
            throw new InvalidOperationException(refusal);
        }

        var vapix = await caller.Vapix.CreateAsync(device.Id, ct).ConfigureAwait(false);
        var parameters = await vapix.ListParametersAsync([app.AppName], ct).ConfigureAwait(false);
        if (!parameters.Keys.Any(k => k.StartsWith(app.AppName + ".", StringComparison.Ordinal)))
        {
            throw new VpnDeviceException($"The {app.DisplayName} app is not installed on this device.");
        }

        return (device, vapix, parameters);
    }

    internal static async Task<SettingsReply> GetSettingsAsync(ICallContext caller, SettingsRequest request, CancellationToken ct)
    {
        var app = Enum.IsDefined(request.Kind) ? VpnApps.Of(request.Kind) : throw new ArgumentException("Choose an app.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(DeviceTimeout);
        var (_, vapix, parameters) = await OpenAppAsync(caller, request.DeviceId, app, timeout.Token).ConfigureAwait(false);
        string? publicKey = null;
        if (app.Kind == VpnKind.WireGuard && parameters.TryGetValue(app.AppName + ".PrivateKey", out var key) && WireGuardKeys.IsKey(key))
        {
            publicKey = WireGuardKeys.PublicKeyOf(key);
        }
        else if (app.Kind == VpnKind.WireGuardServer)
        {
            publicKey = await DeviceVpn.ReadHubPublicKeyAsync(vapix, timeout.Token).ConfigureAwait(false);
        }

        return new SettingsReply(request.DeviceId, app.Kind, AppSettings.Read(app, parameters)) { PublicKey = publicKey };
    }

    /// <summary>
    /// Writes the changed settings of one app on one device in one request. A secret sent empty keeps its value. A new
    /// WireGuard key of a camera behind a hub also goes to the hub's peers.
    /// </summary>
    private async Task<SaveSettingsReply> SaveSettingsAsync(ICallContext caller, SaveSettingsRequest request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(DeviceTimeout * 2);
        var change = await CheckSaveAsync(caller, request, timeout.Token).ConfigureAwait(false);
        return await ApplySaveAsync(caller, change, timeout.Token, ct).ConfigureAwait(false);
    }

    /// <summary>A checked change of one camera's settings: what it writes, on which device.</summary>
    internal sealed record SettingsChange(VpnApp App, IDeviceInfo Device, Oadm.Sdk.Vapix.IVapixClient Vapix, IReadOnlyDictionary<string, string> Changes, string? Profile);

    /// <summary>
    /// The page's checks of a change: only the parameters the device has, valid values, a secret sent empty keeps its value.
    /// Reads the device's parameters, never writes.
    /// </summary>
    internal static async Task<SettingsChange> CheckSaveAsync(ICallContext caller, SaveSettingsRequest request, CancellationToken ct)
    {
        var app = Enum.IsDefined(request.Kind) ? VpnApps.Of(request.Kind) : throw new ArgumentException("Choose an app.");
        var changes = (request.Values ?? new Dictionary<string, string>())
            .Where(v => !(AppSettings.IsSecret(app.Kind, v.Key) && string.IsNullOrEmpty(v.Value)))
            .ToDictionary(v => v.Key, v => v.Value?.Trim() ?? "", StringComparer.Ordinal);
        var profile = app.Kind == VpnKind.OpenVpn && !string.IsNullOrWhiteSpace(request.Profile) ? request.Profile : null;
        if (profile is not null && ConnectRules.ProfileProblem(profile) is { } profileProblem)
        {
            throw new ArgumentException(profileProblem);
        }

        if (changes.Count == 0 && profile is null)
        {
            throw new ArgumentException("Nothing was changed.");
        }

        var (device, vapix, parameters) = await OpenAppAsync(caller, request.DeviceId, app, ct).ConfigureAwait(false);
        var names = parameters.Keys
            .Where(k => k.StartsWith(app.AppName + ".", StringComparison.Ordinal))
            .Select(k => k[(app.AppName.Length + 1)..])
            .ToHashSet(StringComparer.Ordinal);
        if (AppSettings.Validate(app, changes, names) is { Count: > 0 } problems)
        {
            throw new ArgumentException(problems[0].Message);
        }

        return new SettingsChange(app, device, vapix, changes, profile);
    }

    /// <summary>Writes a checked change in one request (the profile first); then reads the device again for the table.</summary>
    internal async Task<SaveSettingsReply> ApplySaveAsync(ICallContext caller, SettingsChange change, CancellationToken deviceCt, CancellationToken ct)
    {
        var (app, device, vapix, changes, profile) = change;
        if (profile is not null)
        {
            await DeviceVpn.UploadOpenVpnProfileAsync(vapix, profile, deviceCt).ConfigureAwait(false);
        }

        if (changes.Count > 0)
        {
            await DeviceVpn.SetParametersAsync(vapix, changes.ToDictionary(c => app.Parameter(c.Key), c => c.Value), deviceCt).ConfigureAwait(false);
        }

        string? note = null;
        if (app.Kind == VpnKind.WireGuard && changes.TryGetValue("PrivateKey", out var privateKey))
        {
            note = await UpdateWireGuardRecordAsync(caller, device, WireGuardKeys.PublicKeyOf(privateKey), ct).ConfigureAwait(false);
        }

        await RefreshDeviceAsync(caller, device, ct).ConfigureAwait(false);
        return new SaveSettingsReply(changes.Count + (profile is null ? 0 : 1), note);
    }

    /// <summary>Keeps the remembered public key of a camera in step with its new private key; tells its hub.</summary>
    private async Task<string?> UpdateWireGuardRecordAsync(ICallContext caller, IDeviceInfo device, string publicKey, CancellationToken ct)
    {
        await _wireGuardGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var records = await LoadRecordsAsync(ct).ConfigureAwait(false);
            var index = records.FindIndex(r => r.DeviceId == device.Id);
            if (index < 0)
            {
                return "Add the camera's new public key to its WireGuard server.";
            }

            var record = records[index] = records[index] with { PublicKey = publicKey };
            await _ctx!.Settings.SetAsync(WireGuardSettingKey, JsonSerializer.Serialize(records, Fleet.Json), ct).ConfigureAwait(false);
            if (record.HubDeviceId is not { } hubId)
            {
                return "Copy the new peer from WireGuard peers to your WireGuard server.";
            }

            if (await caller.Devices.FindAsync(hubId, ct).ConfigureAwait(false) is null)
            {
                return "Add the camera's new public key to its hub camera.";
            }

            var payload = new ConnectPayload
            {
                Settings = new ConnectRequest { Kind = VpnKind.WireGuard, HubDeviceId = hubId },
                HubPeers = [new HubPeer(record.PeerName, publicKey, record.Address)],
            };
            await caller.Tasks.RunAsync(ConnectTaskPlugin.PluginId, [hubId], JsonSerializer.Serialize(payload, Fleet.Json), caller.UserName, ct).ConfigureAwait(false);
            return "The hub camera gets the new public key (see the task list).";
        }
        finally
        {
            _wireGuardGate.Release();
        }
    }

    /// <summary>
    /// WireGuard: a new key pair made here; its private key goes to this camera only (the checks and the write of a save),
    /// the page gets the public key. A camera behind a hub: the hub gets the new public key.
    /// </summary>
    private async Task<NewKeyPairReply> NewKeyPairAsync(ICallContext caller, NewKeyPairRequest request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(DeviceTimeout * 2);
        var keys = WireGuardKeys.Generate();
        var save = new SaveSettingsRequest(request.DeviceId, VpnKind.WireGuard, new Dictionary<string, string> { ["PrivateKey"] = keys.PrivateKey });
        var change = await CheckSaveAsync(caller, save, timeout.Token).ConfigureAwait(false);
        var saved = await ApplySaveAsync(caller, change, timeout.Token, ct).ConfigureAwait(false);
        return new NewKeyPairReply(keys.PublicKey, saved.Note);
    }

    /// <summary>Reads one device again and puts its rows into the table of the caller's group (one rows event).</summary>
    private async Task RefreshDeviceAsync(ICallContext caller, IDeviceInfo device, CancellationToken ct)
    {
        var rows = await ReadDeviceAsync(caller, device, await LatestAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        SetRows(caller.DeviceScopeKey, [(device.Id, rows)]);
    }

    /// <summary>The newest version of each app, from the cache when it is fresh; empty for apps GitHub did not answer for.</summary>
    private async Task<IReadOnlyDictionary<VpnKind, string>> LatestAsync(CancellationToken ct)
    {
        var all = await _catalog.GetAllAsync(checkNow: false, ct).ConfigureAwait(false);
        return all.Where(r => r.Version is not null).ToDictionary(r => r.Kind, r => r.Version!);
    }

    // ---- Read ----

    /// <summary>
    /// Reads the devices and sends their rows as they come, <see cref="Fleet.MaxDevicesPerEvent"/> devices per event; at
    /// the end the devices that were not read leave the table, then the state says the read ended.
    /// </summary>
    private async Task ReadAsync(ICallContext owner, string scope, IReadOnlyList<IDeviceInfo> devices, CancellationToken ct)
    {
        var pending = new List<(Guid, IReadOnlyList<FleetRow>)>();
        void Flush(bool all)
        {
            List<(Guid, IReadOnlyList<FleetRow>)>? batch = null;
            lock (pending)
            {
                if (pending.Count > 0 && (all || pending.Count >= Fleet.MaxDevicesPerEvent))
                {
                    batch = [.. pending];
                    pending.Clear();
                }
            }

            if (batch is not null)
            {
                SetRows(scope, batch);
            }
        }

        string? releaseError = null;
        try
        {
            var releases = await _catalog.GetAllAsync(checkNow: false, ct).ConfigureAwait(false);
            releaseError = releases.FirstOrDefault(r => r.Error is not null)?.Error;
            var latest = releases.Where(r => r.Version is not null).ToDictionary(r => r.Kind, r => r.Version!);
            await Parallel.ForEachAsync(devices, new ParallelOptions { MaxDegreeOfParallelism = ReadParallelism, CancellationToken = ct }, async (device, token) =>
            {
                if (InGroup(scope, device.Id))
                {
                    var rows = await ReadDeviceAsync(owner, device, latest, token).ConfigureAwait(false);
                    lock (pending)
                    {
                        pending.Add((device.Id, rows));
                    }

                    Flush(all: false);
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Flush(all: true);
            EndRead(scope, null, keepReleaseError: true);
            return;
        }

        Flush(all: true);
        var read = devices.Select(d => d.Id).ToHashSet();
        List<Guid> gone;
        lock (_gate)
        {
            gone = [.. TableOf(scope).Rows.Keys.Where(id => !read.Contains(id))];
        }

        SetRows(scope, [.. gone.Select(id => (id, (IReadOnlyList<FleetRow>)[]))]);
        EndRead(scope, releaseError, keepReleaseError: false);
    }

    /// <summary>One row per VPN app on the device, or one row that says there is none or why it was not read.</summary>
    internal static async Task<IReadOnlyList<FleetRow>> ReadDeviceAsync(ICallContext owner, IDeviceInfo device, IReadOnlyDictionary<VpnKind, string> latest, CancellationToken ct)
    {
        var model = device.Model ?? "";
        FleetRow Problem(string error) => new(device.Id, device.Address, model, null, null, VpnState.Unknown, null, null, error);
        if (DeviceMessages.Refusal(device, refuseUnreachable: true) is { } refusal)
        {
            return [Problem(refusal)];
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(DeviceTimeout);
        try
        {
            var vapix = await owner.Vapix.CreateAsync(device.Id, timeout.Token).ConfigureAwait(false);
            var apps = await DeviceVpn.ListAppsAsync(vapix, timeout.Token).ConfigureAwait(false);
            var rows = new List<FleetRow>();
            foreach (var installed in apps)
            {
                if (VpnApps.ByAppName(installed.Name) is not { } app)
                {
                    continue;
                }

                var status = installed.IsRunning
                    ? await DeviceVpn.ReadStatusAsync(vapix, app, timeout.Token).ConfigureAwait(false)
                    : new VpnStatus(VpnState.AppStopped, null, null);
                rows.Add(new FleetRow(device.Id, device.Address, model, app.Kind, installed.Version, status.State, status.Address, status.Detail, null)
                {
                    LatestVersion = latest.GetValueOrDefault(app.Kind),
                });
            }

            return rows.Count > 0 ? rows : [new FleetRow(device.Id, device.Address, model, null, null, VpnState.NoVpnApp, null, null, null)];
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return [Problem(DeviceMessages.TimedOut)];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [Problem(ex.Message)];
        }
    }

    // ---- The table per device group ----

    /// <summary>The table of one device group: the rows per device, the devices in id order for the pages of GetState.</summary>
    private sealed class FleetTable
    {
        public Dictionary<Guid, IReadOnlyList<FleetRow>> Rows { get; } = [];

        public SortedSet<Guid> Order { get; } = [];

        public bool Reading { get; set; }

        public string? ReleaseError { get; set; }
    }

    private static readonly Guid LastId = new("ffffffff-ffff-ffff-ffff-ffffffffffff");

    private bool InGroup(string scope, Guid deviceId) => _ctx!.DeviceScopes?.Contains(scope, deviceId) ?? true;

    /// <summary>The group's devices changed: its table is emptied, the pages drop every row.</summary>
    private void OnGroupChanged(object? sender, string scope)
    {
        lock (_gate)
        {
            var table = TableOf(scope);
            table.Rows.Clear();
            table.Order.Clear();
            PublishState(scope, table, cleared: true);
        }
    }

    /// <summary>Under <see cref="_gate"/>.</summary>
    private FleetTable TableOf(string scope)
    {
        if (!_tables.TryGetValue(scope, out var table))
        {
            _tables[scope] = table = new FleetTable();
        }

        return table;
    }

    /// <summary>One page of a group's table: the devices after <paramref name="after"/> in id order, with the version it shows.</summary>
    private FleetState PageOf(string scope, Guid? after, int limit)
    {
        limit = Math.Clamp(limit, 1, Fleet.StatePageSize);
        lock (_gate)
        {
            var table = TableOf(scope);
            IEnumerable<Guid> ids = after is { } start
                ? table.Order.GetViewBetween(start, LastId).Where(id => id != start)
                : table.Order;
            var rows = new List<FleetRow>();
            Guid? last = null;
            var taken = 0;
            var more = false;
            foreach (var id in ids)
            {
                if (taken == limit)
                {
                    more = true;
                    break;
                }

                taken++;
                last = id;
                if (InGroup(scope, id))
                {
                    rows.AddRange(table.Rows[id]);
                }
            }

            return new FleetState(_version, table.Reading, rows) { ReleaseError = table.ReleaseError, NextAfter = more ? last : null };
        }
    }

    private bool TryBeginRead(string scope)
    {
        lock (_gate)
        {
            var table = TableOf(scope);
            if (table.Reading)
            {
                return false;
            }

            table.Reading = true;
            PublishState(scope, table, cleared: false);
            return true;
        }
    }

    private void EndRead(string scope, string? releaseError, bool keepReleaseError)
    {
        lock (_gate)
        {
            var table = TableOf(scope);
            table.Reading = false;
            if (!keepReleaseError)
            {
                table.ReleaseError = releaseError;
            }

            PublishState(scope, table, cleared: false);
        }
    }

    /// <summary>
    /// Each device now has exactly these rows (none: it leaves the table); sent as rows events of at most
    /// <see cref="Fleet.MaxDevicesPerEvent"/> devices. Rows of devices outside the group are never kept.
    /// </summary>
    private void SetRows(string scope, IReadOnlyList<(Guid Id, IReadOnlyList<FleetRow> Rows)> changes)
    {
        foreach (var chunk in changes.Chunk(Fleet.MaxDevicesPerEvent))
        {
            lock (_gate)
            {
                var table = TableOf(scope);
                var rows = new List<FleetRow>();
                foreach (var (id, deviceRows) in chunk)
                {
                    if (deviceRows.Count > 0 && InGroup(scope, id))
                    {
                        table.Rows[id] = deviceRows;
                        table.Order.Add(id);
                        rows.AddRange(deviceRows);
                    }
                    else
                    {
                        table.Rows.Remove(id);
                        table.Order.Remove(id);
                    }
                }

                // Published under the lock: the events leave in the order of their versions.
                var json = JsonSerializer.Serialize(new FleetRowsEvent(++_version, [.. chunk.Select(c => c.Id)], rows), Fleet.Json);
                _ctx?.Events?.Publish(Fleet.RowsTopic, json, PluginEventAudience.DeviceScope(scope));
            }
        }
    }

    /// <summary>Under <see cref="_gate"/>: whether a read runs, without rows.</summary>
    private void PublishState(string scope, FleetTable table, bool cleared)
    {
        var state = new FleetState(++_version, table.Reading, []) { ReleaseError = table.ReleaseError, Cleared = cleared };
        _ctx?.Events?.Publish(Fleet.StateTopic, Serialize(state), PluginEventAudience.DeviceScope(scope));
    }

    private static string Serialize(FleetState state) => JsonSerializer.Serialize(state, Fleet.Json);

    /// <summary>The GitHub client and the WireGuard lock live as long as the plugin object.</summary>
    public void Dispose()
    {
        _catalog.Dispose();
        _wireGuardGate.Dispose();
    }
}
