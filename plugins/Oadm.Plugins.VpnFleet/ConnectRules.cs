using System.Text.RegularExpressions;

namespace Oadm.Plugins.VpnFleet;

/// <summary>A problem of one field of a <see cref="ConnectRequest"/>, with the text shown below that field.</summary>
public sealed record FieldError(string Field, string Message);

/// <summary>
/// The rules of a <see cref="ConnectRequest"/>: the page shows them below the fields, the server checks them again
/// before any task starts. Only the fields of the chosen VPN count.
/// </summary>
public static partial class ConnectRules
{
    /// <summary>The largest OpenVPN profile (with inline certificates a profile is a few KB).</summary>
    public const int MaxProfileLength = 256 * 1024;

    private const int MaxSecretLength = 512;

    public static IReadOnlyList<FieldError> Validate(ConnectRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new List<FieldError>();
        void Add(string field, string? message)
        {
            if (message is not null)
            {
                errors.Add(new FieldError(field, message));
            }
        }

        switch (request.Kind)
        {
            case VpnKind.Tailscale:
                Add(nameof(ConnectRequest.AuthKey), Secret(request.AuthKey, "Enter an auth key."));
                Add(nameof(ConnectRequest.LoginServer), OptionalUrl(request.LoginServer));
                break;
            case VpnKind.NetBird:
                Add(nameof(ConnectRequest.SetupKey), Secret(request.SetupKey, "Enter a setup key."));
                Add(nameof(ConnectRequest.ManagementUrl), OptionalUrl(request.ManagementUrl));
                break;
            case VpnKind.ZeroTier:
                Add(nameof(ConnectRequest.NetworkId), NetworkIdPattern().IsMatch(request.NetworkId?.Trim() ?? "") ? null : "Enter the 16-character network ID.");
                break;
            case VpnKind.OpenVpn:
                Add(nameof(ConnectRequest.Profile), ProfileProblem(request.Profile));
                Add(nameof(ConnectRequest.UserName), OneLine(request.UserName)
                    ?? (string.IsNullOrEmpty(request.UserName) && !string.IsNullOrEmpty(request.Password) ? "Enter the user name of the password." : null));
                Add(nameof(ConnectRequest.Password), OneLine(request.Password));
                break;
            case VpnKind.WireGuard:
                if (request.HubDeviceId is null)
                {
                    Add(nameof(ConnectRequest.Endpoint), Endpoint(request.Endpoint));
                    Add(nameof(ConnectRequest.ServerPublicKey), WireGuardKeys.IsKey(request.ServerPublicKey) ? null : "Enter the server's public key (44 characters).");
                    Add(nameof(ConnectRequest.AddressPool), WireGuardAddressing.ParsePool(request.AddressPool) is null
                        ? $"Enter a network such as 10.66.0.0/24 (/{WireGuardAddressing.MinPrefix} to /{WireGuardAddressing.MaxPrefix})."
                        : null);
                }

                Add(nameof(ConnectRequest.AllowedIps), WireGuardAddressing.ParseNetworks(request.AllowedIps) is null
                    ? "Enter networks such as 10.66.0.0/24, separated by commas."
                    : null);
                break;
            default:
                Add(nameof(ConnectRequest.Kind), "Choose a VPN.");
                break;
        }

        return errors;
    }

    private static string? Secret(string? value, string missing)
    {
        var text = value?.Trim() ?? "";
        if (text.Length == 0)
        {
            return missing;
        }

        return text.Length > MaxSecretLength || text.Any(char.IsWhiteSpace) ? "The key has spaces or is too long. Paste it again." : null;
    }

    private static string? OptionalUrl(string? value)
    {
        var text = value?.Trim() ?? "";
        return text.Length == 0
            || (Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && !text.Any(char.IsWhiteSpace))
            ? null
            : "Enter an address such as https://vpn.example.com.";
    }

    private static string? OneLine(string? value) =>
        value is not null && (value.Length > MaxSecretLength || value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal))
            ? "Enter one line."
            : null;

    public static string? ProfileProblem(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Choose the VPN profile (.ovpn).";
        }

        if (value.Length > MaxProfileLength)
        {
            return "The profile is larger than 256 KB.";
        }

        if (CommandDirective(value) is { } directive)
        {
            return $"The profile runs commands on the camera ({directive}). Remove that line.";
        }

        return RemoteLine().IsMatch(value) ? null : "This is not an OpenVPN profile: it has no remote server.";
    }

    /// <summary>
    /// The OpenVPN options that run a program or load code on the camera; a profile with one is refused. "setenv opt" and
    /// a leading "--" do not hide them.
    /// </summary>
    public static readonly IReadOnlySet<string> CommandDirectives = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "script-security", "up", "down", "route-up", "route-pre-down", "ipchange", "client-connect", "client-disconnect",
        "learn-address", "auth-user-pass-verify", "tls-verify", "plugin", "iproute",
    };

    /// <summary>The first option of the profile that runs commands, null when it has none. Inline blocks (&lt;ca&gt;) hold data only.</summary>
    public static string? CommandDirective(string profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string? block = null;
        foreach (var raw in profile.Split('\n'))
        {
            var line = raw.Trim();
            if (block is not null)
            {
                if (line.Equals("</" + block + ">", StringComparison.OrdinalIgnoreCase))
                {
                    block = null;
                }

                continue;
            }

            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            if (line.StartsWith('<') && line.EndsWith('>') && !line.StartsWith("</", StringComparison.Ordinal))
            {
                var tag = line[1..^1].Trim();
                if (!tag.Equals("connection", StringComparison.OrdinalIgnoreCase))
                {
                    block = tag; // data such as a certificate, never options
                }

                continue;
            }

            var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var first = words[0].TrimStart('-');
            if (first.Equals("setenv", StringComparison.OrdinalIgnoreCase) && words.Length > 2 && words[1].Equals("opt", StringComparison.OrdinalIgnoreCase))
            {
                first = words[2].TrimStart('-');
            }

            if (CommandDirectives.Contains(first))
            {
                return first.ToLowerInvariant();
            }
        }

        return null;
    }

    private static string? Endpoint(string? value)
    {
        var text = value?.Trim() ?? "";
        var colon = text.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(text[(colon + 1)..], out var port) || port is < 1 or > 65535)
        {
            return "Enter the server's address and port, such as vpn.example.com:51820.";
        }

        var host = text[..colon].Trim('[', ']');
        return host.Length > 0 && !host.Any(char.IsWhiteSpace) ? null : "Enter the server's address and port, such as vpn.example.com:51820.";
    }

    [GeneratedRegex("^[0-9a-fA-F]{16}$")]
    private static partial Regex NetworkIdPattern();

    [GeneratedRegex(@"^\s*(remote\s+\S+|<connection>)", RegexOptions.Multiline)]
    private static partial Regex RemoteLine();
}
