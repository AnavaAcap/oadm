using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Network.Model;

/// <summary>
/// Task payload of "Network settings...". Every section is optional: null means "keep unchanged",
/// so only the sections the user touched are written. Per-device values (IPv4 address from the
/// range assignment, host name from the template) are resolved by the dialog into <see cref="Devices"/>.
/// The same payload is validated in the dialog and again on the server before anything is written.
/// </summary>
[Description("Network settings of devices. A section you leave out stays as it is.")]
public sealed record NetworkPayload
{
    [Description("The IPv4 settings.")]
    public Ipv4Change? Ipv4 { get; init; }

    [Description("The IPv6 settings.")]
    public Ipv6Change? Ipv6 { get; init; }

    [Description("The DNS settings.")]
    public DnsChange? Dns { get; init; }

    [Description("The host names.")]
    public HostNameChange? HostName { get; init; }

    /// <summary>One entry per selected device, keyed by OADM device id.</summary>
    [Description("The devices that get the settings, by device id. Give each one its own address or host name; an empty object for a device that gets only the shared settings. A device of the filter that is not listed is left out.")]
    public IReadOnlyDictionary<Guid, DeviceAssignment> Devices { get; init; } = new Dictionary<Guid, DeviceAssignment>();

    [JsonIgnore]
    public bool HasChanges => Ipv4 is not null || Ipv6 is not null || Dns is not null || HostName is not null;

    public string ToJson() => JsonSerializer.Serialize(this, PayloadJson.Options);

    /// <summary>Parses the payload; throws <see cref="NetworkValidationException"/> for malformed JSON.</summary>
    public static NetworkPayload Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new NetworkValidationException(["The task has no settings to apply."]);
        }

        try
        {
            return JsonSerializer.Deserialize<NetworkPayload>(json, PayloadJson.Options)
                ?? throw new NetworkValidationException(["The task has no settings to apply."]);
        }
        catch (JsonException ex)
        {
            throw new NetworkValidationException(["The task settings could not be read: " + ex.Message]);
        }
    }
}

public enum Ipv4Mode
{
    Dhcp = 0,
    Static = 1,
}

/// <summary>IPv4 change. For <see cref="Ipv4Mode.Static"/> the address comes per device from <see cref="DeviceAssignment.Ipv4Address"/>.</summary>
[Description("IPv4: DHCP, or a static address per device.")]
public sealed record Ipv4Change(
    [property: Description("Dhcp, or Static with the address of each device in devices.")] Ipv4Mode Mode,
    [property: Description("Static: the subnet mask as a prefix length, 1 to 30 (24 is 255.255.255.0)."), Range(1, 30)] int? PrefixLength = null,
    [property: Description("Static: the default router, in the same subnet."), MaxLength(15)] string? Gateway = null);

public enum Ipv6Mode
{
    Disabled = 0,

    /// <summary>Router advertisements (SLAAC), DHCPv6 as advertised by the router.</summary>
    Auto = 1,

    /// <summary>Stateful DHCPv6.</summary>
    Dhcp = 2,

    /// <summary>Manual address: <see cref="DeviceAssignment.Ipv6Address"/> per device (or <see cref="Ipv6Change.Address"/> for all).</summary>
    Static = 3,
}

/// <summary>
/// IPv6 change. For <see cref="Ipv6Mode.Static"/> the address comes per device from <see cref="DeviceAssignment.Ipv6Address"/>;
/// <see cref="Address"/> is the fallback for a device without one (one device). Prefix length and gateway are shared.
/// </summary>
[Description("IPv6: off, automatic, DHCPv6 or a static address.")]
public sealed record Ipv6Change(
    [property: Description("Disabled, Auto (router advertisements), Dhcp (stateful DHCPv6) or Static.")] Ipv6Mode Mode,
    [property: Description("Static: the address for a device without its own in devices."), MaxLength(45)] string? Address = null,
    [property: Description("Static: the prefix length, 1 to 128."), Range(1, 128)] int? PrefixLength = null,
    [property: Description("Static: the default router."), MaxLength(45)] string? Gateway = null)
{
    /// <summary>The static IPv6 address of one device: its own entry, else the shared <see cref="Address"/>.</summary>
    public string? AddressFor(DeviceAssignment? entry) =>
        string.IsNullOrWhiteSpace(entry?.Ipv6Address) ? Address?.Trim() : entry.Ipv6Address.Trim();
}

/// <summary>
/// DNS resolver change. When <see cref="UseDhcp"/> is true the static fields are not written. With
/// <see cref="KeepDomains"/> only the servers change: each device keeps its own static domain name and search
/// domains (read in "Read current settings"), used by "Assign IP address...".
/// </summary>
[Description("DNS: from DHCP, or static servers.")]
public sealed record DnsChange(
    [property: Description("Use the DNS settings DHCP gives; then the other fields are not written.")] bool UseDhcp,
    [property: Description("At most 2 DNS servers (IPv4 addresses)."), MaxLength(PayloadValidator.MaxDnsServers)] IReadOnlyList<string>? Servers = null,
    [property: Description("The domain name."), MaxLength(253)] string? DomainName = null,
    [property: Description("At most 6 search domains."), MaxLength(PayloadValidator.MaxSearchDomains)] IReadOnlyList<string>? SearchDomains = null,
    [property: Description("Change only the servers; each device keeps its own domain name and search domains.")] bool KeepDomains = false);

/// <summary>Host name change. When <see cref="UseDhcp"/> is false the name comes per device from <see cref="DeviceAssignment.HostName"/>.</summary>
[Description("Host names: from DHCP, or one per device.")]
public sealed record HostNameChange([property: Description("Use the host name DHCP gives; false: the host name of each device in devices.")] bool UseDhcp);

[Description("What one device gets of its own.")]
public sealed record DeviceAssignment(
    [property: Description("Static IPv4: the address of this device."), MaxLength(15)] string? Ipv4Address = null,
    [property: Description("The host name of this device: letters, digits and hyphens, 1 to 63 characters."), MaxLength(63)] string? HostName = null,
    [property: Description("Static IPv6: the address of this device."), MaxLength(45)] string? Ipv6Address = null);

/// <summary>Input is invalid. Thrown before anything is written, so the message says so.</summary>
public sealed class NetworkValidationException : Exception
{
    public NetworkValidationException(IReadOnlyList<string> errors)
        : base(string.Join(" ", errors ?? []) + " Nothing was changed.")
    {
        Errors = errors ?? [];
    }

    public NetworkValidationException()
        : this([])
    {
    }

    public NetworkValidationException(string message)
        : this([message])
    {
    }

    public NetworkValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
        Errors = [message];
    }

    public IReadOnlyList<string> Errors { get; }
}

internal static class PayloadJson
{
    public static readonly JsonSerializerOptions Options = PluginJson.CreateOptions(PluginJsonEnums.CamelCase);
}

/// <summary>
/// The input of "Assign IP address" for AI agents (docs/spec/mcp.md): the IPv4 section, optionally the DNS servers, and the
/// devices. The payload the dialog writes; the task reads the same text as a <see cref="NetworkPayload"/>.
/// </summary>
[Description("Assign IP addresses: DHCP, or a static address per device. IPv6 and host names are Network settings.")]
public sealed record AssignIpPayload(
    [property: Description("The IPv4 settings.")] Ipv4Change Ipv4,
    [property: Description("The devices, by device id: each one its own IPv4 address with Static, an empty object with Dhcp. A device of the filter that is not listed is left out.")]
    IReadOnlyDictionary<Guid, DeviceAssignment> Devices,
    [property: Description("The DNS servers; the devices keep their domain name and search domains.")] DnsChange? Dns = null);
