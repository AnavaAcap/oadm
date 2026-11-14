using System.Globalization;
using System.Net;
using System.Net.Sockets;

using Oadm.Core.Discovery;
using Oadm.Core.Sites;
using Oadm.Sdk.Network;

namespace Oadm.Server.Discovery;

/// <summary>
/// The networks an AI agent may scan or probe (docs/spec/mcp.md "Add devices"): the networks of the server's own interfaces
/// (up, not loopback) and the networks of the sites whose node is online. The Network range page of the client lets a
/// technician type any range; an agent gets only these, so it cannot use OADM to look at (or send a login to) addresses
/// the server was never meant to manage.
/// </summary>
internal static class OwnNetworks
{
    /// <summary>The answer text naming what is allowed (no site names: they are user texts).</summary>
    public static string Allowed(IServerNetworkInterfaces interfaces)
    {
        ArgumentNullException.ThrowIfNull(interfaces);
        var own = Own(interfaces).Select(n => n.Text).Distinct(StringComparer.Ordinal).Take(8).ToList();
        var where = own.Count == 0 ? "no network of the server" : "the server's own networks (" + string.Join(", ", own) + ")";
        return $"Use IP addresses inside {where} or inside the networks of connected sites.";
    }

    /// <summary>The routes a look at <paramref name="first"/>..<paramref name="last"/> may use; null when no allowed network holds all of it.</summary>
    public static DiscoveryRoutes? RoutesFor(DiscoveryRoutes allowed, IServerNetworkInterfaces interfaces, ISiteNetwork? sites, IPAddress first, IPAddress last)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        ArgumentNullException.ThrowIfNull(interfaces);
        var local = allowed.Local && Own(interfaces).Any(n => n.Holds(first) && n.Holds(last));
        HashSet<Guid> viaSites = [];
        if (sites is not null)
        {
            foreach (var site in sites.OnlineSites().Where(allowed.Allows))
            {
                if (sites.Reaches(site, first) && sites.Reaches(site, last))
                {
                    viaSites.Add(site);
                }
            }
        }

        return local || viaSites.Count > 0 ? new DiscoveryRoutes(local, viaSites) : null;
    }

    private static IEnumerable<Network> Own(IServerNetworkInterfaces interfaces)
    {
        foreach (var card in interfaces.List().Where(c => c.IsUp && !c.IsLoopback))
        {
            foreach (var address in card.Addresses)
            {
                if (card.PrefixLengthOf(address) is { } prefix && !address.IsIPv6LinkLocal && !IPAddress.IsLoopback(address))
                {
                    yield return new Network(address, prefix);
                }
            }
        }
    }

    private readonly record struct Network(IPAddress Address, int Prefix)
    {
        public string Text => string.Create(CultureInfo.InvariantCulture, $"{Masked(Address, Prefix)}/{Prefix}");

        public bool Holds(IPAddress other)
        {
            var mapped = other.IsIPv4MappedToIPv6 ? other.MapToIPv4() : other;
            return mapped.AddressFamily == Address.AddressFamily && Masked(mapped, Prefix).Equals(Masked(Address, Prefix));
        }

        private static IPAddress Masked(IPAddress address, int prefix)
        {
            var bytes = address.GetAddressBytes();
            var bits = Math.Min(prefix, bytes.Length * 8);
            for (var i = 0; i < bytes.Length; i++)
            {
                var keep = Math.Clamp(bits - (i * 8), 0, 8);
                bytes[i] &= (byte)(0xFF << (8 - keep));
            }

            return new IPAddress(bytes);
        }
    }

    /// <summary>True for an IPv4 or IPv6 address literal without a scheme, port, path or user info (what an agent may name).</summary>
    public static bool TryBareAddress(string? text, out IPAddress address)
    {
        address = IPAddress.None;
        var value = text?.Trim() ?? string.Empty;
        if (value.Length == 0 || value.Contains("://", StringComparison.Ordinal) || value.Contains('/', StringComparison.Ordinal) || value.Contains('%', StringComparison.Ordinal)
            || value.Contains('[', StringComparison.Ordinal))
        {
            return false; // "http://...", a path, a scope id, brackets (a port follows them)
        }

        if (!IPAddress.TryParse(value, out var parsed) || parsed.IsIPv4MappedToIPv6)
        {
            return false;
        }

        if (parsed.AddressFamily == AddressFamily.InterNetwork && (value.Count(c => c == '.') != 3 || value.Contains(':', StringComparison.Ordinal)))
        {
            return false; // "10.1" and "167772161" parse as addresses but are not typed so; "10.0.0.1:8443" has a port
        }

        if (parsed.AddressFamily != AddressFamily.InterNetwork && parsed.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        address = parsed;
        return true;
    }
}
