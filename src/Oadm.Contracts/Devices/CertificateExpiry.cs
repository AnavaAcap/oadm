using System.Globalization;

namespace Oadm.Contracts.Devices;

/// <summary>
/// The text of the Certificate expires column (docs/spec/main-window.md): shared by the client's grid and the server's tools,
/// so both say the same for a device.
/// </summary>
public static class CertificateExpiry
{
    /// <summary>
    /// Certificates expiring within this many days count as expiring soon when the server does not say (a server without
    /// PKI); else each device's days of PKI's Expiry warning policy (<c>cert_warning_days</c>).
    /// </summary>
    public const int WarningDays = 30;

    /// <summary>
    /// "245 days", "1 day", "Today" (less than a day left), "Expired today", "Expired 1 day ago",
    /// "Expired 3 days ago", "Never" (RFC 5280 "no well-defined expiration", 9999-12-31, e.g. the Axis device ID
    /// certificate); empty when there is no certificate (HTTP only or not checked).
    /// </summary>
    public static string Text(DateTime? notAfterUtc, DateTime nowUtc)
    {
        if (notAfterUtc is not { } notAfter)
        {
            return string.Empty;
        }

        if (NeverExpires(notAfter))
        {
            return "Never";
        }

        TimeSpan left = notAfter - nowUtc;
        if (left < TimeSpan.Zero)
        {
            int ago = (int)Math.Floor(-left.TotalDays);
            return ago switch
            {
                0 => "Expired today",
                1 => "Expired 1 day ago",
                _ => string.Create(CultureInfo.InvariantCulture, $"Expired {ago} days ago"),
            };
        }

        int days = (int)Math.Floor(left.TotalDays);
        return days switch
        {
            0 => "Today",
            1 => "1 day",
            _ => string.Create(CultureInfo.InvariantCulture, $"{days} days"),
        };
    }

    /// <summary>RFC 5280 4.1.2.5: NotAfter 99991231235959Z means the certificate has no well-defined expiration date.</summary>
    public static bool NeverExpires(DateTime notAfterUtc) => notAfterUtc.Year >= 9999;

    /// <summary>Ended before <paramref name="nowUtc"/>.</summary>
    public static bool Expired(DateTime notAfterUtc, DateTime nowUtc) => !NeverExpires(notAfterUtc) && notAfterUtc < nowUtc;

    /// <summary>
    /// Not ended yet but within <paramref name="warningDays"/> of its end (0 or less: <see cref="WarningDays"/>): the
    /// column's warning chip.
    /// </summary>
    public static bool ExpiresSoon(DateTime notAfterUtc, DateTime nowUtc, int warningDays) =>
        !NeverExpires(notAfterUtc) && notAfterUtc >= nowUtc && (notAfterUtc - nowUtc).TotalDays <= (warningDays > 0 ? warningDays : WarningDays);
}
