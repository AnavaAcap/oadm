using System.Globalization;

namespace Oadm.Contracts.Settings;

/// <summary>
/// The rules and texts of the Settings page's server fields (docs/spec/settings-backup.md), shared by the page and the agents'
/// settings tool (docs/spec/mcp.md "Server settings"), so both refuse the same values with the same words.
/// </summary>
public static class ServerSettingsRules
{
    public const int MinScanParallelism = 1;
    public const int MaxScanParallelism = 256;
    public const int MinScanTimeoutMs = 100;
    public const int MaxScanTimeoutMs = 30_000;
    public const int MinZeroConfSeconds = 5;
    public const int MaxZeroConfSeconds = 300;
    public const int MinWaitingRetryMinutes = 1;
    public const int MaxWaitingRetryMinutes = 1440;
    public const int MinParallelTasks = 1;
    public const int MaxParallelTasks = 256;
    public const int MinClockTolerance = 1;
    public const int MaxClockTolerance = 3600;

    /// <summary>The longest server name.</summary>
    public const int MaxServerNameLength = 255;

    public const string EnterServerName = "Enter a server name.";
    public const string EnterListenUrl = "Enter a listen URL, e.g. https://0.0.0.0:5080.";
    public const string ListenUrlLike = "Enter a URL like https://0.0.0.0:5080.";

    /// <summary>The choices of Update the list online.</summary>
    public static readonly IReadOnlyList<string> VulnerabilityUpdates = ["off", "server", "clients"];

    /// <summary>"Enter a whole number from 5 to 300." for an empty or out-of-range number field.</summary>
    public static string? RangeError(decimal? value, int min, int max) =>
        value is null || value < min || value > max || decimal.Truncate(value.Value) != value
            ? string.Create(CultureInfo.CurrentCulture, $"Enter a whole number from {min} to {max}.")
            : null;

    /// <summary>An empty server name.</summary>
    public static string? ServerNameError(string? name) => (name ?? string.Empty).Trim().Length == 0 ? EnterServerName : null;

    /// <summary>The listen URL Kestrel binds to: http(s), a host (also * or +) and optionally a port.</summary>
    public static string? ListenUrlError(string? url)
    {
        var text = (url ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return EnterListenUrl;
        }

        var probe = text.Replace("://*", "://localhost", StringComparison.Ordinal).Replace("://+", "://localhost", StringComparison.Ordinal);
        return Uri.TryCreate(probe, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? null
            : ListenUrlLike;
    }
}
