using Oadm.Plugins.Pki.Policies;
using Oadm.Sdk.Certificates;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// The server policy MCP server (docs/spec/mcp.md "Transport and certificate", docs/spec/pki.md "Server certificates"): a system
/// policy on every server, not a PRO part, that gets its own "MCP Server Certificate" on the first Turn on (the host's first
/// load) also without OADM PRO and never the shared 802.1X and EST server certificate, refusing a certificate that names none of the server's addresses or its host name, and the host's
/// certificate through <see cref="IServerCertificates"/>.
/// </summary>
public sealed class McpServerPolicyTests
{
    [Fact]
    public async Task Every_server_has_the_system_policy_AI_agents_and_its_first_Turn_on_makes_its_own_certificate_also_without_OADM_PRO()
    {
        await using var pki = await PkiHarness.StartAsync(pro: false);
        var policy = Assert.Single(pki.Policies.PoliciesOf(PolicyKinds.McpServer));
        Assert.Equal("MCP server", policy.Name);
        Assert.Equal("MCP server", PolicyKinds.Name(PolicyKinds.McpServer));
        Assert.Null(policy.CertificateId);
        Assert.True(PolicyStore.IsSystem(policy, pki.Policies.All));
        Assert.False(PolicyKinds.IsPro(PolicyKinds.McpServer));
        Assert.Equal("The MCP server has no certificate yet.", PolicyStore.Summary(policy, new Dictionary<string, string>()));

        var refused = await pki.Policies.DeleteAsync(policy.Id, "admin", CancellationToken.None);
        Assert.Equal(PolicyTexts.SystemPolicy, refused.Error?.Message);
        var second = await pki.Policies.SaveAsync(new PkiPolicy { Name = "Second", Kind = PolicyKinds.McpServer, CertificateId = "x" }, "admin", PkiService.CipherSuites, CancellationToken.None);
        Assert.NotNull(second.Error);

        // The first Turn on (the host loads the certificate only while it is on): its own certificate, naming this computer.
        pki.Policies.McpServerNames = () => ["10.0.0.17", Environment.MachineName];
        var host = pki.Core.FakeServices.Find<IServerCertificates>()!;
        var load = await host.LoadAsync(HostServers.Mcp, CancellationToken.None);
        string own;
        try
        {
            Assert.Null(load.Problem);
            Assert.True(load.Chain[0].HasPrivateKey);
            Assert.Equal(PkiService.McpServerCertificateName, load.Chain[0].GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false));
            // The server's addresses and name, and the loopback names: an agent on this computer connects to localhost.
            Assert.Equal(new[] { "10.0.0.17", "127.0.0.1", "::1", Environment.MachineName, "localhost" }.Order(StringComparer.Ordinal), PkiService.NamesOf(load.Chain[0]).Order(StringComparer.Ordinal));
            Assert.Contains(load.Chain[0].Extensions.OfType<System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>(),
                o => o.Value == "1.3.6.1.5.5.7.3.1"); // server authentication
            using (var key = System.Security.Cryptography.X509Certificates.ECDsaCertificateExtensions.GetECDsaPublicKey(load.Chain[0]))
            {
                Assert.Equal(256, key?.KeySize); // ECC P-256
            }
            own = pki.Policies.FirstOf(PolicyKinds.McpServer)!.CertificateId!;
        }
        finally
        {
            foreach (var certificate in load.Chain)
            {
                certificate.Dispose();
            }
        }

        // A second load makes none again; the PRO servers' shared certificate is another one and never replaces it.
        (await host.LoadAsync(HostServers.Mcp, CancellationToken.None)).Chain.ToList().ForEach(c => c.Dispose());
        Assert.Single(pki.Service.ServerCertificateChoices(), c => c.Name == PkiService.McpServerCertificateName);
        await pki.Service.EnsureServerCertificatesAsync(null, CancellationToken.None);
        Assert.Equal(own, pki.Policies.FirstOf(PolicyKinds.McpServer)!.CertificateId);
        Assert.NotEqual(own, pki.Policies.FirstOf(PolicyKinds.EstServer)!.CertificateId);

        // Its Certificate field changes it afterwards; the next load keeps the user's choice.
        Assert.Null((await CertificateListTests.CreateAsync(pki, name: "agents", addresses: Environment.MachineName)).Error);
        var chosen = pki.Service.ServerCertificateChoices().Single(c => c.Name == "agents").Id;
        var current = pki.Policies.FirstOf(PolicyKinds.McpServer)!;
        Assert.Null((await pki.Policies.SaveAsync(current with { CertificateId = chosen }, "admin", PkiService.CipherSuites, CancellationToken.None)).Error);
        var again = await host.LoadAsync(HostServers.Mcp, CancellationToken.None);
        try
        {
            Assert.Equal("agents", again.Chain[0].GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false));
        }
        finally
        {
            foreach (var certificate in again.Chain)
            {
                certificate.Dispose();
            }
        }

        Assert.Equal(chosen, pki.Policies.FirstOf(PolicyKinds.McpServer)!.CertificateId);
    }

    [Fact]
    public async Task With_OADM_PRO_a_start_with_AI_agents_off_makes_or_assigns_nothing_for_its_policy()
    {
        await using var pki = await PkiHarness.StartAsync();
        await pki.Service.EnsureServerCertificatesAsync(null, CancellationToken.None); // OADM PRO: the start, a license change, the server plugins
        var shared = pki.Policies.FirstOf(PolicyKinds.EstServer)!.CertificateId;
        Assert.NotNull(shared);
        Assert.Null(pki.Policies.FirstOf(PolicyKinds.McpServer)!.CertificateId);
        Assert.DoesNotContain(pki.Service.ServerCertificateChoices(), c => c.Name == PkiService.McpServerCertificateName);

        // Turn on of the MCP server afterwards: its own certificate, never the shared one the PRO servers show.
        var host = pki.Core.FakeServices.Find<IServerCertificates>()!;
        (await host.LoadAsync(HostServers.Mcp, CancellationToken.None)).Chain.ToList().ForEach(c => c.Dispose());
        var own = pki.Policies.FirstOf(PolicyKinds.McpServer)!.CertificateId;
        Assert.NotNull(own);
        Assert.NotEqual(shared, own);
        Assert.Equal(PkiService.McpServerCertificateName, pki.Service.ServerCertificateChoices().Single(c => c.Id == own).Name);
    }

    [Fact]
    public async Task The_policy_takes_only_a_certificate_that_names_this_server_and_the_host_gets_it_with_its_key_and_issuer()
    {
        await using var pki = await PkiHarness.StartAsync();
        pki.Policies.McpServerNames = () => ["10.0.0.17", "SERVER01"];
        Assert.Null((await CertificateListTests.CreateAsync(pki, name: "nvr.example.com", addresses: "nvr.example.com, 10.0.0.20")).Error);
        Assert.Null((await CertificateListTests.CreateAsync(pki, name: "server01", addresses: "server01, 10.0.0.17")).Error);
        var other = pki.Service.ServerCertificateChoices().Single(c => c.Name == "nvr.example.com").Id;
        var own = pki.Service.ServerCertificateChoices().Single(c => c.Name == "server01").Id;
        var policy = pki.Policies.FirstOf(PolicyKinds.McpServer)!;

        var wrong = await pki.Policies.SaveAsync(policy with { CertificateId = other }, "admin", PkiService.CipherSuites, CancellationToken.None);
        Assert.Equal(PolicyFields.Certificate, wrong.Error?.Field);
        Assert.Equal(PolicyTexts.McpServerNamesNotInCertificate, wrong.Error?.Message);

        var host = pki.Core.FakeServices.Find<IServerCertificates>()!;
        var changed = 0;
        host.Changed += (_, _) => changed++;
        var saved = await pki.Policies.SaveAsync(policy with { CertificateId = own }, "admin", PkiService.CipherSuites, CancellationToken.None);
        Assert.Null(saved.Error);
        Assert.True(changed > 0);

        var load = await host.LoadAsync(HostServers.Mcp, CancellationToken.None);
        try
        {
            Assert.Null(load.Problem);
            Assert.True(load.Chain[0].HasPrivateKey);
            Assert.Equal(load.Chain[1].Subject, load.Chain[0].Issuer); // the issuer follows (the CA agents trust)
        }
        finally
        {
            foreach (var certificate in load.Chain)
            {
                certificate.Dispose();
            }
        }

        // The certificate the policy shows cannot be deleted; another server than the MCP server has no policy here.
        Assert.Equal(PolicyKinds.McpServer, pki.Policies.ShowingCertificate(own)?.Kind);
        Assert.NotNull((await host.LoadAsync("other", CancellationToken.None)).Problem);

        // The host's full name names it too (SERVER01 as server01.example.com); another host's full name does not.
        Assert.Null((await CertificateListTests.CreateAsync(pki, name: "server01.example.com", addresses: "server01.example.com")).Error);
        Assert.Null((await CertificateListTests.CreateAsync(pki, name: "server02.example.com", addresses: "server02.example.com")).Error);
        var full = pki.Service.ServerCertificateChoices().Single(c => c.Name == "server01.example.com").Id;
        var elsewhere = pki.Service.ServerCertificateChoices().Single(c => c.Name == "server02.example.com").Id;
        var current = pki.Policies.FirstOf(PolicyKinds.McpServer)!;
        Assert.Equal(PolicyTexts.McpServerNamesNotInCertificate,
            (await pki.Policies.SaveAsync(current with { CertificateId = elsewhere }, "admin", PkiService.CipherSuites, CancellationToken.None)).Error?.Message);
        Assert.Null((await pki.Policies.SaveAsync(current with { CertificateId = full }, "admin", PkiService.CipherSuites, CancellationToken.None)).Error);
    }
}
