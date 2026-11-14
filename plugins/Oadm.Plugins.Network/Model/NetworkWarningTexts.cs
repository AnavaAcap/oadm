namespace Oadm.Plugins.Network.Model;

/// <summary>The reachability warnings of a risky network change, shared by both dialogs and the preview of the tools.</summary>
public static class NetworkWarningTexts
{
    public const string Dhcp =
        "Devices may get another address. OADM finds them again. A DHCP server must be on their network.";

    public const string MaskAndRouter =
        "A wrong subnet mask or default router makes devices unreachable from the server. Make sure the server can reach the new addresses.";

    /// <summary>How many devices move to a new IP address, and that OADM follows them.</summary>
    public static string Moving(int moving, int total) =>
        moving == 1 && total == 1
            ? "The device gets a new IP address. OADM follows it."
            : $"{moving} of {total} devices get a new IP address. OADM follows them.";
}
