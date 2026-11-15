using Oadm.Plugins.NtpServer.Tasks;
using Oadm.Plugins.NtpServer.Tools;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.NtpServer;

/// <summary>
/// Core plugin "NTP server" ("NTP server" in CLAUDE.md): an RFC 5905 server-mode responder on the selected
/// interface, answering from the server clock (stratum 10, "LOCL") or an optional upstream (stratum + 1). Settings are
/// stored server side and restored on start. Contributes the task "Use OADM as NTP server".
/// Page backend methods: <see cref="NtpServerMethods"/>; live events: state and request log. Device groups (SDK 0.4.0): the
/// server is server-wide, so Save is for users with all devices; users with a group see the page with the requests and
/// sites of their devices only.
/// </summary>
public sealed class NtpServerPlugin : ICorePlugin, IAsyncDisposable
{
    private readonly NtpServerOptions _options;
    private volatile NtpServerService? _service;

    public NtpServerPlugin()
        : this(new NtpServerOptions())
    {
    }

    public NtpServerPlugin(NtpServerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        TaskPlugins = [new UseOadmNtpServerTask(() => _service, _options.Time)];
    }

    public string Id => NtpServerPluginInfo.PluginId;

    public string DisplayName => NtpServerPluginInfo.DisplayName;

    public string? IconKey => NtpServerPluginInfo.IconKey;
    public CorePluginGroup Group => CorePluginGroup.Servers;

    public IReadOnlyList<ITaskPlugin> TaskPlugins { get; }

    /// <summary>The running server (null while stopped).</summary>
    public NtpServerService? Service => _service;

    public async Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        await StopAsync(ct).ConfigureAwait(false);
        var service = new NtpServerService(_options, ctx.Settings, ctx.Events, ctx.Logger, ctx.Firewall, ctx.Network, ctx.Sites, ctx.DeviceScopes);
        await service.StartAsync(ct).ConfigureAwait(false);
        _service = service;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_service is { } service)
        {
            _service = null;
            await service.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Reading the state is for everyone; Save changes the server (Admin, audited).</summary>
    public UserRole RequiredRole(string method)
    {
        method = NtpPageTools.Set.RulesMethodOf(method);
        return method == NtpServerMethods.GetState || method == NtpTools.Status.Method ? UserRole.Operator : UserRole.Admin;
    }

    /// <summary>The Admin methods and the run of the settings tool are audited; a preview only reads.</summary>
    public bool IsAudited(string method) => RequiredRole(method) == UserRole.Admin && !PluginTool.IsPreviewMethod(method);

    /// <summary>The status tool (docs/spec/mcp.md "Plugins"): the page's state, for the caller's devices.</summary>
    public IReadOnlyList<PluginTool> Tools { get; } = [.. NtpTools.All, .. NtpPageTools.Set.Tools];

    /// <summary>The running service; refuses a call while the plugin is stopped.</summary>
    internal NtpServerService RunningService => _service ?? throw new InvalidOperationException("The NTP server plugin is not running.");

    /// <summary>The tools of this plugin are named <c>oadm_ntp_*</c>.</summary>
    public string? ToolArea => "ntp";

    /// <summary>Changing the server is one right ("manage", the Admin methods); the page itself is "show".</summary>
    public IReadOnlyList<PermissionDefinition> Permissions { get; } =
    [
        new(PermissionKeys.Manage, "Change the NTP server", "Turn the NTP server on or off and choose its network and time source.") { OperatorDefault = false },
    ];

    /// <summary>The NTP server serves every device: changing it is for users with all devices.</summary>
    public bool RequiresAllDevices(string method) => NtpPageTools.Set.RulesMethodOf(method) == NtpServerMethods.Save;

    /// <summary>A user's call: users with a device group read the state of their devices (requests, sites).</summary>
    public async Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var service = _service ?? throw new InvalidOperationException("The NTP server plugin is not running.");
        if (method == NtpTools.Status.Method)
        {
            // Read only, with the page's own rules: the requests and sites of the caller's devices.
            return NtpTools.Answer(caller.HasAllDevices
                ? service.GetState(full: true)
                : service.GetState(Serving.NtpGroupView.From(await caller.Devices.ListAsync(ct).ConfigureAwait(false))));
        }

        if (NtpPageTools.Set.Handles(method))
        {
            if (!caller.HasAllDevices)
            {
                throw new DeviceAccessRefusedException(); // the host refuses it first (RequiresAllDevices)
            }

            return await NtpPageTools.Set.InvokeAsync(this, caller, method, payloadJson, ct).ConfigureAwait(false);
        }

        if (caller.HasAllDevices)
        {
            return await InvokeAsync(method, payloadJson, ct).ConfigureAwait(false);
        }

        switch (method)
        {
            case NtpServerMethods.GetState:
                service.Viewers.Seen(caller);
                var view = Serving.NtpGroupView.From(await caller.Devices.ListAsync(ct).ConfigureAwait(false));
                return NtpJson.Serialize(service.GetState(view) with { Renew = service.Viewers.Renews });
            case NtpServerMethods.Save:
                throw new DeviceAccessRefusedException(); // the host refuses it first (RequiresAllDevices)
            default:
                throw new ArgumentException($"Unknown method '{method}'.", nameof(method));
        }
    }

    public async Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct)
    {
        var service = _service ?? throw new InvalidOperationException("The NTP server plugin is not running.");
        switch (method)
        {
            case NtpServerMethods.GetState:
                return NtpJson.Serialize(service.GetState(full: true));
            case NtpServerMethods.Save:
                return NtpJson.Serialize(await service.SaveAsync(NtpJson.Deserialize<SaveRequest>(payloadJson), ct).ConfigureAwait(false));
            default:
                throw new ArgumentException($"Unknown method '{method}'.", nameof(method));
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);
}
