using System.Globalization;

namespace Oadm.Contracts.Mcp;

/// <summary>
/// The texts of the server AI agents connect to (docs/spec/mcp.md), shared by the server and the client's Settings card AI
/// agents, so one concept has one phrase.
/// </summary>
public static class McpTexts
{
    public const string PortRange = "Enter a port from 1025 to 65535.";
    public const string NoPki = "PKI is not running. The server log says why.";
    public const string Off = "Off";
    public const string UnknownInterface = "This interface is not available. Choose another one.";
    public const string NoCaCertificate = "The server for AI agents has no certificate yet.";

    /// <summary>The certificate line of the card while it is off: the first Turn on gives the policy one (docs/spec/mcp.md).</summary>
    public const string CertificateOnTurnOn = "Turn on gets it from the PKI policy AI agents.";

    /// <summary>The certificate's chain is not known to PKI: agents cannot be given a CA.</summary>
    public const string NoIssuer = "PKI does not know the CA of this certificate.";

    public static string Running(int port) => string.Create(CultureInfo.InvariantCulture, $"Running on port {port}");

    public static string PortProblem(int port, string why) => string.Create(CultureInfo.InvariantCulture, $"Port {port} cannot be opened: {why}");

    public static string InterfaceGone(string name) => $"Interface {name} is not available";
}
