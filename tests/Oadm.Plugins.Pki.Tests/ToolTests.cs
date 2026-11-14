using Oadm.Plugins.Pki.Certificates;
using Oadm.Plugins.Pki.Client;
using Oadm.Plugins.Pki.Policies;
using Oadm.Plugins.Pki.Tools;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>Who may call the methods of the tools (docs/spec/mcp.md "Plugins"): the reads like the page's, Create like the page's Create.</summary>
public sealed class ToolTests
{
    [Fact]
    public void The_reads_are_for_operators_and_Create_needs_the_rights_of_the_page_with_a_preview_that_leaves_no_audit_entry()
    {
        ICorePlugin plugin = new PkiPlugin();
        Assert.Equal(["certificates", "policies", "certificate_create"], plugin.Tools.Select(t => t.Name));
        Assert.Equal("pki", plugin.ToolArea ?? "pki");
        foreach (var read in new[] { PkiTools.Certificates.Method, PkiTools.Policies.Method })
        {
            Assert.Equal(UserRole.Operator, plugin.RequiredRole(read));
            Assert.False(plugin.IsAudited(read));
            Assert.False(plugin.RequiresAllDevices(read)); // a group reads its own devices' certificates
        }

        var create = PkiTools.CertificateCreate;
        foreach (var method in new[] { create.PreviewMethod, create.RunMethod })
        {
            Assert.Equal(UserRole.Admin, plugin.RequiredRole(method));
            Assert.True(plugin.RequiresAllDevices(method));
            Assert.Null(plugin.RequiredPermission(method)); // "Manage the certificate authority", like the page's Create
        }

        Assert.False(plugin.IsAudited(create.PreviewMethod)); // it only checks
        Assert.True(plugin.IsAudited(create.RunMethod));
        Assert.True(create.Changes);
        Assert.Equal(UserRole.Admin, plugin.RequiredRole(CertificateMethods.Create));
    }

    [Fact]
    public async Task Every_tool_without_Changes_and_every_preview_leaves_the_stored_state_as_it_was()
    {
        var ctx = new FakeCoreContext { Secrets = TestSecrets.Create() };
        ctx.Add(new FakeDevice { Address = "10.0.0.48", Serial = "B8A44F631339" });
        var trust = new FakeTrustStore();
        await using var plugin = new PkiPlugin(PkiHarness.Options(trust));
        await plugin.StartAsync(ctx, CancellationToken.None);
        await plugin.Service!.Background;
        var admin = ctx.CallAsAllDevices();
        var policy = plugin.Service.Policies.FirstOf(PolicyKinds.HttpsCertificate)!.Id;
        Assert.True(PkiJson.Deserialize<CertificateActionReply>(await ctx.InvokeAsync(plugin, admin, CertificateMethods.Create,
            PkiJson.Serialize(new CreateCertificateRequest(Oadm.Sdk.Certificates.DeviceCertificatePurposes.Https, policy, "x.example.com", "x.example.com")))).Ok);

        string Stored() =>
            string.Join('\n', ctx.PluginSettings.Values.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value))
            + "\nrecord writes: " + ctx.PluginRecords.WrittenRecords
            + "\nlocal certificates: " + plugin.Service.Local.List.Count
            + "\ntrust store installs: " + trust.Installs + "/" + trust.Installed.Count;
        var before = Stored();

        ICorePlugin core = plugin;
        Assert.Contains(core.Tools, t => t.Changes); // the create tool is in the list
        foreach (var tool in core.Tools)
        {
            var method = tool.Changes ? tool.PreviewMethod : tool.Method;
            var payloads = tool.Changes
                ? new[] { """{"use":"https","name":"Gate","addresses":["10.0.0.5","gate.example"]}""", """{"use":"mqtt","name":"Gate","addresses":[]}""" }
                : new[] { null, """{"limit":1}""" };
            foreach (var payload in payloads)
            {
                Assert.NotNull(await ctx.InvokeAsync(plugin, admin, method, payload));
            }
        }

        Assert.Equal(before, Stored());
    }

}
