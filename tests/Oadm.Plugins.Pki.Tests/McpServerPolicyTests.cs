using Oadm.Plugins.Pki.Policies;
using Oadm.Sdk.Certificates;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// The server policy MCP server (docs/spec/mcp.md "Transport and certificate", docs/spec/pki.md "Server certificates"): a
/// system policy without a certificate on every server, not a PRO part, refusing a certificate that names none of the
/// server's addresses or its host name, and the host's certificate through <see cref="IServerCertificates"/>.
/// </summary>
public sealed class McpServerPolicyTests
{
    [Fact]
    public async Task Every_server_has_the_system_policy_MCP_server_without_a_certificate_also_without_OADM_PRO()
    {
        await using var pki = await PkiHarness.StartAsync(pro: false);
        var policy = Assert.Single(pki.Policies.PoliciesOf(PolicyKinds.McpServer));
        Assert.Equal(PolicyLimits.McpServerName, policy.Name);
        Assert.Null(policy.CertificateId);
        Assert.True(PolicyStore.IsSystem(policy, pki.Policies.All));
        Assert.False(PolicyKinds.IsPro(PolicyKinds.McpServer));
        Assert.Equal("The MCP server for AI agents has no certificate yet.", PolicyStore.Summary(policy, new Dictionary<string, string>()));

        var refused = await pki.Policies.DeleteAsync(policy.Id, "admin", CancellationToken.None);
        Assert.Equal(PolicyTexts.SystemPolicy, refused.Error?.Message);
        var second = await pki.Policies.SaveAsync(new PkiPolicy { Name = "Second", Kind = PolicyKinds.McpServer, CertificateId = "x" }, "admin", PkiService.CipherSuites, CancellationToken.None);
        Assert.NotNull(second.Error);

        var host = pki.Core.FakeServices.Find<IServerCertificates>()!;
        var load = await host.LoadAsync(HostServers.Mcp, CancellationToken.None);
        Assert.Empty(load.Chain);
        Assert.Equal("The MCP server has no certificate. Choose one in its policy.", load.Problem);
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
