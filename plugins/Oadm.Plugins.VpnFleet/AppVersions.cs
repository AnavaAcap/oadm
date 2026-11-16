using Oadm.Plugins.Acap;

namespace Oadm.Plugins.VpnFleet;

/// <summary>
/// Version numbers of the apps ("1.104.7", tag "v1.104.7"), compared like Applications (ACAP) does
/// (<see cref="AcapCompatibility.CompareVersions"/>), except that a pre-release comes before its release: "1.2.3-beta" is
/// older than "1.2.3".
/// </summary>
public static class AppVersions
{
    /// <summary>The version starts with a number.</summary>
    public static bool IsReadable(string? version) => AcapCompatibility.HasNumericVersion(Core(version));

    public static int Compare(string? a, string? b)
    {
        var core = AcapCompatibility.CompareVersions(Core(a), Core(b));
        if (core != 0)
        {
            return core;
        }

        // Same numbers: a release is newer than its pre-release ("1.2.3" > "1.2.3-beta").
        var (pa, pb) = (PreRelease(a), PreRelease(b));
        return (pa, pb) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            _ => Math.Sign(string.Compare(pa, pb, StringComparison.OrdinalIgnoreCase)),
        };
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is a later version than <paramref name="installed"/>. An installed version that
    /// cannot be read never stops an upgrade.
    /// </summary>
    public static bool IsNewer(string candidate, string? installed) =>
        IsReadable(candidate) && (!IsReadable(installed) || Compare(candidate, installed) > 0);

    /// <summary>"v1.104.7" gives "1.104.7".</summary>
    public static string FromTag(string tag) => tag.Trim().TrimStart('v', 'V');

    /// <summary>"v1.2.3-beta+5" gives "1.2.3".</summary>
    private static string Core(string? version)
    {
        var text = FromTag(version ?? "");
        var end = text.IndexOfAny(['-', '+']);
        return end < 0 ? text : text[..end];
    }

    /// <summary>"1.2.3-beta+5" gives "beta"; null for a release.</summary>
    private static string? PreRelease(string? version)
    {
        var text = FromTag(version ?? "");
        var plus = text.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            text = text[..plus];
        }

        var dash = text.IndexOf('-', StringComparison.Ordinal);
        return dash < 0 || dash == text.Length - 1 ? null : text[(dash + 1)..];
    }
}
