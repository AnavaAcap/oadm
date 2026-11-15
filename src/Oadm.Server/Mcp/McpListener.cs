using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

using Microsoft.AspNetCore.Builder;

using Oadm.Contracts.Mcp;
using Oadm.Core.Plugins;
using Oadm.Core.Settings;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Network;

namespace Oadm.Server.Mcp;

/// <summary>What the Settings card shows of the MCP server.</summary>
/// <param name="Certificate">"802.1X and EST server (OADM Root CA SERVER01), until 2036-11-14"; null without one or while off.</param>
public sealed record McpStatus(McpSettings Settings, bool Running, string Text, bool PkiAvailable, string? Certificate, IReadOnlyList<string> CertificateNames, IReadOnlyList<byte[]> CaCertificates);

/// <summary>
/// Opens and closes the MCP server's port (docs/spec/mcp.md "Transport and certificate"): while the setting is on, PKI runs and
/// its policy MCP server picks a usable certificate (the first Turn on makes the MCP Server Certificate for it), the
/// server listens on the port and interface; otherwise the port is closed and the status says why. It follows the setting (Save on the card), PKI's certificate (a change of the policy or the
/// Certificates list), PKI being turned on or off and, every 30 s, whatever it could not open before (a port in use, an
/// interface that is gone). The Windows service keeps a firewall rule for the port only while it listens.
/// </summary>
public sealed partial class McpListener : IAsyncDisposable
{
    /// <summary>How often a server that is on but not listening tries again.</summary>
    public static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(30);

    private readonly IServiceProvider _services;
    private readonly ServerSettingsStore _store;
    private readonly PluginServiceRegistry _plugins;
    private readonly IServerNetworkInterfaces _interfaces;
    private readonly IFirewallRules? _firewall;
    private readonly TimeProvider _time;
    private readonly ILogger<McpListener> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ITimer? _timer;
    private IServerCertificates? _watched;
    private WebApplication? _app;
    private string? _running; // what the open port was started with (port, addresses, certificate)
    private FirewallRuleKeeper? _rule;
    private bool _firewallChecked;
    private bool _stopped;

    public McpListener(IServiceProvider services, ServerSettingsStore store, PluginServiceRegistry plugins, TimeProvider time, ILogger<McpListener> logger,
        IServerNetworkInterfaces? interfaces = null, IFirewallRules? firewall = null)
    {
        _services = services;
        _store = store;
        _plugins = plugins;
        _time = time;
        _logger = logger;
        _interfaces = interfaces ?? SystemNetworkInterfaces.Instance;
        _firewall = firewall;
        Status = new McpStatus(new McpSettings(), false, McpTexts.Off, false, null, [], []);
    }

    /// <summary>Replaces Kestrel (tests: an in-memory server, no network).</summary>
    public Action<WebApplicationBuilder>? ConfigureHost { get; init; }

    /// <summary>The state now (the Settings card).</summary>
    public McpStatus Status { get; private set; }

    /// <summary>The web host while the port is open (tests reach it through its test server).</summary>
    public WebApplication? App => _app;

    /// <summary>Raised after every change of <see cref="Status"/>.</summary>
    public event EventHandler? Changed;

    /// <summary>The server's interfaces (the choices of Listen on).</summary>
    public IReadOnlyList<ServerNetworkInterface> Interfaces() =>
        // Only interfaces an agent can reach: up, with an address that is not IPv6 link-local (no filter drivers, virtual adapters
        // without an address, tunnels). The loopback stays: an agent on this computer only.
        [.. _interfaces.List().Where(n => n.IsUp && n.Addresses.Any(a => !a.IsIPv6LinkLocal))];

    /// <summary>Starts following the setting and PKI; opens the port when it is on.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        _timer = _time.CreateTimer(_ => _ = ApplyInBackgroundAsync(), null, RetryEvery, RetryEvery);
        await ApplyAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Brings the port in line with the setting and the certificate now.</summary>
    public async Task ApplyAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_stopped)
            {
                return;
            }

            var settings = await McpSettings.LoadAsync(_store, ct).ConfigureAwait(false);
            if (!_firewallChecked && _firewall is not null)
            {
                // Like every rule keeper: the first sync closes a rule a crash left; opening follows below when it listens.
                _firewallChecked = true;
                await new FirewallRuleKeeper(Rule(settings.Port), _firewall, _logger).SyncAsync(false, CancellationToken.None).ConfigureAwait(false);
            }

            var certificates = _plugins.Get<IServerCertificates>();
            Watch(certificates);
            if (certificates is null)
            {
                await CloseAsync().ConfigureAwait(false);
                Set(new McpStatus(settings, false, settings.Enabled ? McpTexts.NoPki : McpTexts.Off, false, null, [], []));
                return;
            }

            if (!settings.Enabled)
            {
                // Off: PKI is not asked (its first load makes the policy's own certificate, "MCP Server Certificate").
                await CloseAsync().ConfigureAwait(false);
                Set(new McpStatus(settings, false, McpTexts.Off, true, null, [], []));
                return;
            }

            var loaded = await certificates.LoadAsync(HostServers.Mcp, ct).ConfigureAwait(false);
            try
            {
                var leaf = loaded.Chain.Count > 0 ? loaded.Chain[0] : null;
                var label = leaf is null ? null : Label(leaf);
                var names = leaf is null ? [] : NamesOf(leaf);
                IReadOnlyList<byte[]> cas = [.. loaded.Chain.Skip(1).Select(c => c.RawData)];
                if (leaf is null)
                {
                    await CloseAsync().ConfigureAwait(false);
                    Set(new McpStatus(settings, false, loaded.Problem ?? McpTexts.NoCaCertificate, true, null, [], []));
                    return;
                }

                IReadOnlyList<IPAddress>? addresses = null;
                if (McpSettings.Normal(settings.ListenOn) is { } id)
                {
                    var nic = _interfaces.List().FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.Ordinal));
                    addresses = nic is null ? null : [.. nic.Addresses.Where(a => !(a.AddressFamily == AddressFamily.InterNetworkV6 && a.IsIPv6LinkLocal))];
                    if (addresses is not { Count: > 0 })
                    {
                        await CloseAsync().ConfigureAwait(false);
                        Set(new McpStatus(settings, false, McpTexts.InterfaceGone(nic?.Name ?? id), true, label, names, cas));
                        return;
                    }
                }

                var wanted = string.Join('|', settings.Port, addresses is null ? "*" : string.Join(',', addresses), leaf.Thumbprint, string.Join(',', loaded.Chain.Skip(1).Select(c => c.Thumbprint)));
                if (_app is null || !string.Equals(_running, wanted, StringComparison.Ordinal))
                {
                    await CloseAsync().ConfigureAwait(false);
                    var problem = await OpenAsync(new McpListenOptions(settings.Port, addresses, leaf, [.. loaded.Chain.Skip(1)]), ct).ConfigureAwait(false);
                    if (problem is not null)
                    {
                        Set(new McpStatus(settings, false, McpTexts.PortProblem(settings.Port, problem), true, label, names, cas));
                        return;
                    }

                    _running = wanted;
                    loaded = loaded with { Chain = [] }; // the open port keeps its certificates
                }

                await SyncFirewallAsync(settings.Port, open: true).ConfigureAwait(false);
                Set(new McpStatus(settings, true, McpTexts.Running(settings.Port), true, label, names, cas));
            }
            finally
            {
                foreach (var certificate in loaded.Chain)
                {
                    certificate.Dispose();
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Closes the port for good (the server stops).</summary>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _stopped = true;
            _timer?.Dispose();
            Watch(null);
            await CloseAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> OpenAsync(McpListenOptions listen, CancellationToken ct)
    {
        var app = McpEndpoint.Build(_services, ConfigureHost is null ? listen : null, ConfigureHost, NamesOf(listen.Certificate), listen.Port);
        try
        {
            await app.StartAsync(ct).ConfigureAwait(false);
            _app = app;
            _held = [listen.Certificate, .. listen.Issuers];
            LogListening(listen.Port);
            return null;
        }
#pragma warning disable CA1031 // A port in use or a refused bind must not stop the server; the card says why.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogOpenFailed(ex, listen.Port);
            await app.DisposeAsync().ConfigureAwait(false);
            foreach (var certificate in new[] { listen.Certificate }.Concat(listen.Issuers))
            {
                certificate.Dispose();
            }

            return ex is IOException { InnerException: SocketException socket } ? socket.Message : ex.Message;
        }
    }

    private List<X509Certificate2> _held = [];

    private async Task CloseAsync()
    {
        if (_app is { } app)
        {
            _app = null;
            _running = null;
            try
            {
                await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                await app.DisposeAsync().ConfigureAwait(false);
                _held.ForEach(c => c.Dispose());
                _held = [];
            }

            LogClosed();
        }

        if (_rule is { } rule)
        {
            await rule.SyncAsync(false, CancellationToken.None).ConfigureAwait(false);
            _rule = null;
        }
    }

    private async Task SyncFirewallAsync(int port, bool open)
    {
        if (_firewall is null)
        {
            return;
        }

        if (_rule is { } old && old.Rule.Port != port)
        {
            await old.SyncAsync(false, CancellationToken.None).ConfigureAwait(false);
            _rule = null;
        }

        _rule ??= new FirewallRuleKeeper(Rule(port), _firewall, _logger);
        await _rule.SyncAsync(open, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>"OADM Server (MCP server, TCP 5082)".</summary>
    private static FirewallRule Rule(int port) => FirewallRule.ForService("MCP server", FirewallProtocol.Tcp, port);

    private void Watch(IServerCertificates? certificates)
    {
        if (ReferenceEquals(_watched, certificates))
        {
            return;
        }

        if (_watched is not null)
        {
            _watched.Changed -= OnCertificatesChanged;
        }

        _watched = certificates;
        if (certificates is not null)
        {
            certificates.Changed += OnCertificatesChanged;
        }
    }

    private void OnCertificatesChanged(object? sender, EventArgs e) => _ = ApplyInBackgroundAsync();

    /// <summary>A change seen outside a call (PKI, the timer, a plugin turned on or off): applied in the background, failures logged.</summary>
    public Task ApplyInBackgroundAsync() => Core.Auth.CallerContext.RunDetached(async () =>
    {
        try
        {
            await ApplyAsync(CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A failure is logged and tried again by the timer.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogApplyFailed(ex);
        }
    });

    private void Set(McpStatus status)
    {
        var changed = Status != status;
        Status = status;
        if (changed)
        {
            Core.Auth.CallerContext.Raise(Changed, this, EventArgs.Empty);
        }
    }

    /// <summary>"802.1X and EST server (OADM Root CA SERVER01), until 2036-11-14".</summary>
    private static string Label(X509Certificate2 certificate) =>
        $"{certificate.GetNameInfo(X509NameType.SimpleName, false)} ({certificate.GetNameInfo(X509NameType.SimpleName, true)}), until {certificate.NotAfter.ToUniversalTime():yyyy-MM-dd}";

    private static IReadOnlyList<string> NamesOf(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault() is { } san
            ? [.. san.EnumerateIPAddresses().Select(ip => ip.ToString()), .. san.EnumerateDnsNames()]
            : [];

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP server: listening on port {Port}")]
    private partial void LogListening(int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP server: port closed")]
    private partial void LogClosed();

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP server: port {Port} cannot be opened")]
    private partial void LogOpenFailed(Exception ex, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP server: the port could not be brought in line with the setting")]
    private partial void LogApplyFailed(Exception ex);
}
