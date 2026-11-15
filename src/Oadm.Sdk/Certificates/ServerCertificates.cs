using System.Security.Cryptography.X509Certificates;

namespace Oadm.Sdk.Certificates;

/// <summary>The servers the host runs whose certificate a PKI server policy picks (<see cref="IServerCertificates"/>).</summary>
public static class HostServers
{
    /// <summary>The MCP server (its policy "MCP server").</summary>
    public const string Mcp = "mcp";
}

/// <summary>
/// The certificates of the servers the host runs itself (SDK 0.13.0; <see cref="HostServers"/>), offered by PKI through
/// <see cref="Plugins.IPluginServices"/>: each is the certificate the server's policy picks from PKI's Certificates list. By
/// convention only the host reads it. Nothing enforces that: any core plugin in the process can find this service, private
/// keys included.
/// </summary>
public interface IServerCertificates
{
    /// <summary>
    /// The certificate of <paramref name="server"/> with its private key, then its issuers (the issuer first); or, without a
    /// usable one, why not (the server's status). The host calls it only while the server is on: like the Turn on of a PKI
    /// server, a policy without a certificate first gets the shared server certificate. The caller disposes the certificates.
    /// </summary>
    Task<HostServerCertificate> LoadAsync(string server, CancellationToken ct);

    /// <summary>The policies or the Certificates list changed: a server loads its certificate again.</summary>
    event EventHandler? Changed;
}

/// <summary>The answer of <see cref="IServerCertificates.LoadAsync"/>.</summary>
/// <param name="Chain">The certificate with its private key, then its issuers; empty when <paramref name="Problem"/> is set.</param>
/// <param name="Problem">Why the server has no certificate it can use ("The MCP server has no certificate. Choose one in its policy."); null when it has one.</param>
public sealed record HostServerCertificate(IReadOnlyList<X509Certificate2> Chain, string? Problem);
