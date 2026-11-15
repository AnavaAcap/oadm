using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Oadm.Client.Settings;

/// <summary>
/// One client configuration of the card's Connect an agent, one compact row: the client, where it goes, Copy and, for a file,
/// Download.
/// </summary>
/// <param name="Title">The client, for example "Claude Code".</param>
/// <param name="Hint">Where the text goes; null for the command, which shows itself.</param>
/// <param name="Text">The command or file content.</param>
/// <param name="FileName">The name Download saves the file as; null for the command (no Download).</param>
public sealed record AgentConnection(string Title, string? Hint, string Text, string? FileName = null)
{
    /// <summary>The tooltip of its Copy button.</summary>
    public string CopyTip => "Copy " + Title;

    /// <summary>The tooltip of its Download button.</summary>
    public string DownloadTip => "Download " + FileName;

    /// <summary>The row is a file: it has Download.</summary>
    public bool IsFile => FileName is not null;

    /// <summary>The row shows its text in one line (the command).</summary>
    public bool ShowsText => FileName is null;

    /// <summary>What the row shows next to its title: the command, or where the file goes.</summary>
    public string Line => ShowsText ? Text : Hint ?? string.Empty;
}

/// <summary>
/// The client configurations of the Settings card AI, section MCP (docs/spec/mcp.md "Connect an agent") for this server's address
/// and port. The access key is never in them: Claude Code and the <c>.mcp.json</c> read it from the variable
/// <c>OADM_KEY</c>, VS Code asks for it (<c>password</c> input). Claude Desktop is not listed: no bridge has been tested against this server.
/// </summary>
public static class AgentConnections
{
    /// <summary>The variable the agent's computer holds the access key in.</summary>
    public const string KeyVariable = "OADM_KEY";

    /// <summary>Shown below the configurations: the agent's computer trusts the CA once.</summary>
    public const string TrustCa = "Download the CA certificate and trust it on the agent's computer. Claude Code reads the file named in NODE_EXTRA_CA_CERTS.";

    /// <summary>
    /// The name agents connect to: a name of the certificate, else one of its addresses (agents refuse a name the certificate
    /// does not hold). Null while the certificate names are not known.
    /// </summary>
    public static string? HostOf(IReadOnlyList<string> certificateNames)
    {
        ArgumentNullException.ThrowIfNull(certificateNames);
        // localhost and the loopback addresses are for an agent on the server's own computer, never the address to hand out.
        var names = certificateNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim())
            .Where(n => !string.Equals(n, "localhost", StringComparison.OrdinalIgnoreCase) && !(IPAddress.TryParse(n, out var a) && IPAddress.IsLoopback(a)))
            .ToList();
        var chosen = names.FirstOrDefault(n => !IPAddress.TryParse(n, out _)) ?? names.FirstOrDefault();
        return chosen is not null && IPAddress.TryParse(chosen, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + chosen + "]" : chosen;
    }

    /// <summary>The endpoint of the server.</summary>
    public static string Url(string host, int port) => string.Create(CultureInfo.InvariantCulture, $"https://{host}:{port}/mcp");

    /// <summary>The configurations, in the order of the card.</summary>
    public static IReadOnlyList<AgentConnection> For(string host, int port)
    {
        var url = Url(host, port);
        return
        [
            new("Claude Code", null,
                $"claude mcp add --transport http oadm {url} --header \"Authorization: Bearer ${{{KeyVariable}}}\""),
            new(".mcp.json", "For a project folder",
                $$"""
                {
                  "mcpServers": {
                    "oadm": {
                      "type": "http",
                      "url": "{{url}}",
                      "headers": { "Authorization": "Bearer ${{{KeyVariable}}}" }
                    }
                  }
                }
                """, ".mcp.json"),
            new("VS Code", "For .vscode/mcp.json",
                $$"""
                {
                  "servers": {
                    "oadm": {
                      "type": "http",
                      "url": "{{url}}",
                      "headers": { "Authorization": "Bearer ${input:oadm-key}" }
                    }
                  },
                  "inputs": [
                    { "type": "promptString", "id": "oadm-key", "description": "OADM access key", "password": true }
                  ]
                }
                """, "mcp.json"),
        ];
    }
}
