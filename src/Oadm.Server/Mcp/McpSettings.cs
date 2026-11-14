using System.Globalization;

using Oadm.Contracts.Mcp;
using Oadm.Core.Settings;
using Oadm.Sdk.Network;

namespace Oadm.Server.Mcp;

/// <summary>
/// The stored settings of the MCP server (setting <see cref="Key"/>; docs/spec/mcp.md "Transport and certificate"): off by
/// default, TCP 5082, every interface. Changed only on the Settings card (Administrator, audited).
/// </summary>
public sealed record McpSettings
{
    /// <summary>The key of the setting.</summary>
    public const string Key = "Mcp";

    public const int DefaultPort = 5082;

    /// <summary>The ports allowed (like the EST server: no rights needed on Linux).</summary>
    public const int MinPort = 1025;
    public const int MaxPort = 65535;

    public bool Enabled { get; init; }

    public int Port { get; init; } = DefaultPort;

    /// <summary>The interface to listen on; null for every interface (<see cref="InterfaceOptions.IsAll"/>).</summary>
    public string? ListenOn { get; init; }

    /// <summary>The stored settings; the defaults when none are stored or the stored value cannot be read (off).</summary>
    public static async Task<McpSettings> LoadAsync(ServerSettingsStore store, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        try
        {
            return await store.GetAsync<McpSettings>(Key, ct).ConfigureAwait(false) ?? new McpSettings();
        }
        catch (System.Text.Json.JsonException)
        {
            return new McpSettings(); // fail closed: off
        }
    }

    public Task SaveAsync(ServerSettingsStore store, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.SetAsync(Key, this, ct);
    }

    /// <summary>The refusal of a port, null when it is allowed.</summary>
    public static string? PortError(int port) => port is >= MinPort and <= MaxPort ? null : McpTexts.PortRange;

    /// <summary>"Turn on, port 5082 -> 5090" for the audit log.</summary>
    public static string Changes(McpSettings before, McpSettings after, Func<string?, string> interfaceName)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(interfaceName);
        var changes = new List<string>();
        if (before.Enabled != after.Enabled)
        {
            changes.Add(after.Enabled ? "Turned on" : "Turned off");
        }

        if (before.Port != after.Port)
        {
            changes.Add(string.Create(CultureInfo.InvariantCulture, $"Port {before.Port} -> {after.Port}"));
        }

        if (!string.Equals(Normal(before.ListenOn), Normal(after.ListenOn), StringComparison.Ordinal))
        {
            changes.Add($"Listen on {interfaceName(before.ListenOn)} -> {interfaceName(after.ListenOn)}");
        }

        return changes.Count == 0 ? "No change" : string.Join(", ", changes);
    }

    /// <summary>The interface id as stored: null for every interface.</summary>
    public static string? Normal(string? listenOn) => InterfaceOptions.IsAll(listenOn) ? null : listenOn!.Trim();
}
