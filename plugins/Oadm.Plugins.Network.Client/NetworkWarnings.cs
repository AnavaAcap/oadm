using Oadm.Plugins.Network.Model;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Network.Client;

/// <summary>The reachability warnings both network dialogs show in the confirmation before a risky change.</summary>
public static class NetworkWarnings
{
    public const string Dhcp = NetworkWarningTexts.Dhcp;

    public const string MaskAndRouter = NetworkWarningTexts.MaskAndRouter;

    /// <summary>How many devices move, and that OADM follows them; empty addresses count as unchanged.</summary>
    public static IEnumerable<string> Static(IReadOnlyList<IDeviceInfo> devices, IReadOnlyList<string> addresses)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(addresses);
        var moving = devices.Where((d, i) => !string.Equals(d.Address, addresses.ElementAtOrDefault(i), StringComparison.OrdinalIgnoreCase)).Count();
        if (moving > 0)
        {
            yield return NetworkWarningTexts.Moving(moving, devices.Count);
        }

        yield return MaskAndRouter;
    }
}
