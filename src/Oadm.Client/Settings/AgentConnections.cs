using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Oadm.Client.Settings;

/// <summary>One client configuration of the card's Connect an agent: what it is for and the text to copy.</summary>
/// <param name="Title">The client, for example "Claude Code".</param>
/// <param name="Hint">Where the text goes.</param>
/// <param name="Text">The command or file content.</param>
public sealed record AgentConnection(string Title, string Hint, string Text)
{
    /// <summary>The tooltip of its Copy button.</summary>
    public string CopyTip => "Copy " + Title;
}

/// <summary>
/// The client configurations of the Settings card AI agents (docs/spec/mcp.md "Connect an agent") for this server's address
/// and port. The access key is never in them: Claude Code and the <c>.mcp.json</c> read it from the variable
/// <c>OADM_KEY</c>, VS Code asks for it (<c>password</c> input), Claude Desktop reads it from its own setting.
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
        var names = certificateNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList();
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
            new("Claude Code", "Run in a terminal.",
                $"claude mcp add --transport http oadm {url} --header \"Authorization: Bearer ${{{KeyVariable}}}\""),
            new(".mcp.json", "In the project folder.",
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
                """),
            new("Claude Desktop", "In claude_desktop_config.json. Replace the two placeholders.",
                $$"""
                {
                  "mcpServers": {
                    "oadm": {
                      "command": "npx",
                      "args": ["-y", "mcp-remote", "{{url}}", "--header", "Authorization:${OADM_AUTH}"],
                      "env": {
                        "OADM_AUTH": "Bearer <access key>",
                        "NODE_EXTRA_CA_CERTS": "<path of oadm-ca.pem>"
                      }
                    }
                  }
                }
                """),
            new("VS Code", "In .vscode/mcp.json. VS Code asks for the key.",
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
                """),
        ];
    }
}
