namespace Oadm.Plugins.Firmware;

/// <summary>The warnings of a factory default, shared by the dialog and the preview of the tools.</summary>
public static class FirmwareWarnings
{
    public const string Soft =
        "Soft factory default: all settings, users and passwords are reset; the IP address and network settings are kept. OADM loses access until a new password is set.";

    public const string Hard =
        "Hard factory default: everything is reset, including the IP address. Devices with a static address fall back to DHCP or 192.168.0.90 and may become unreachable for OADM.";

    /// <summary>The warning of <paramref name="mode"/>; null without a factory default.</summary>
    public static string? Of(FactoryDefaultMode mode) => mode switch
    {
        FactoryDefaultMode.Soft => Soft,
        FactoryDefaultMode.Hard => Hard,
        _ => null,
    };
}
