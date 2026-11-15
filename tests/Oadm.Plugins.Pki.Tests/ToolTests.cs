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
        Assert.Equal(["certificates", "policies", "certificate_create", "certificate_file", "ca_file"], plugin.Tools.Select(t => t.Name).Take(5)); // then the page actions (PageToolTests)
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
    public async Task The_file_tools_hand_out_the_certificate_as_pem_or_der_and_never_a_key()
    {
        var ctx = new FakeCoreContext { Secrets = TestSecrets.Create() };
        var trust = new FakeTrustStore();
        await using var plugin = new PkiPlugin(PkiHarness.Options(trust));
        await plugin.StartAsync(ctx, CancellationToken.None);
        await plugin.Service!.Background;
        ICorePlugin core = plugin;
        var admin = ctx.CallAsAllDevices();
        var policy = plugin.Service.Policies.FirstOf(PolicyKinds.HttpsCertificate)!.Id;
        var created = PkiJson.Deserialize<CertificateActionReply>(await ctx.InvokeAsync(plugin, admin, CertificateMethods.Create,
            PkiJson.Serialize(new CreateCertificateRequest(Oadm.Sdk.Certificates.DeviceCertificatePurposes.Https, policy, "x.example.com", "x.example.com"))));
        Assert.True(created.Ok);

        foreach (var tool in new[] { PkiTools.CertificateFile, PkiTools.CaFile })
        {
            Assert.True(tool.ReturnsFile);
            Assert.False(tool.Changes);
            Assert.Equal(PermissionKeys.Manage, tool.Permission);
            Assert.Equal(UserRole.Admin, core.RequiredRole(tool.Method)); // like the page's Export
            Assert.True(core.RequiresAllDevices(tool.Method));
            Assert.DoesNotContain("pkcs12", tool.InputType!.GetProperties().SelectMany(p => p.GetCustomAttributes(typeof(System.ComponentModel.DataAnnotations.AllowedValuesAttribute), false))
                .Cast<System.ComponentModel.DataAnnotations.AllowedValuesAttribute>().SelectMany(a => a.Values).OfType<string>());
        }

        async Task<System.Text.Json.Nodes.JsonNode> CallAsync(PluginTool tool, string payload) =>
            System.Text.Json.Nodes.JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, tool.Method, payload))!)!;

        foreach (var (format, marker) in new[] { ("pem", "-----BEGIN CERTIFICATE-----"), ("der", null as string) })
        {
            var answer = await CallAsync(PkiTools.CertificateFile, $$"""{"id":"{{created.Id}}","format":"{{format}}"}""");
            var file = answer["file"]!;
            Assert.Equal(CertificateMethods.Download, file["method"]!.GetValue<string>());
            var request = PkiJson.Deserialize<DownloadCertificateRequest>(file["payload_json"]!.GetValue<string>());
            Assert.Equal((created.Id, format, null as string), (request.Id, request.Format, request.Password));
            await using var download = await plugin.OpenDownloadAsync(admin, file["method"]!.GetValue<string>(), file["payload_json"]!.GetValue<string>(), CancellationToken.None);
            using var copy = new MemoryStream();
            await download.Content.CopyToAsync(copy);
            var text = System.Text.Encoding.ASCII.GetString(copy.ToArray());
            Assert.DoesNotContain("PRIVATE KEY", text, StringComparison.Ordinal);
            if (marker is not null)
            {
                Assert.StartsWith(marker, text, StringComparison.Ordinal);
            }
            else
            {
                using var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(copy.ToArray());
                Assert.False(certificate.HasPrivateKey);
            }
        }

        var key = await CallAsync(PkiTools.CertificateFile, $$"""{"id":"{{created.Id}}","format":"pkcs12"}""");
        Assert.Equal("format", Assert.Single(key["errors"]!.AsArray())!["field"]!.GetValue<string>());
        var noId = await CallAsync(PkiTools.CertificateFile, "{}");
        Assert.Equal("id", Assert.Single(noId["errors"]!.AsArray())!["field"]!.GetValue<string>());

        var ca = (await CallAsync(PkiTools.CaFile, "{}"))["file"]!;
        Assert.Equal(PkiMethods.ExportPublic, ca["method"]!.GetValue<string>());
        await using (var download = await plugin.OpenDownloadAsync(admin, ca["method"]!.GetValue<string>(), ca["payload_json"]!.GetValue<string>(), CancellationToken.None))
        {
            using var copy = new MemoryStream();
            await download.Content.CopyToAsync(copy);
            var text = System.Text.Encoding.ASCII.GetString(copy.ToArray());
            Assert.StartsWith("-----BEGIN CERTIFICATE-----", text, StringComparison.Ordinal);
            Assert.DoesNotContain("PRIVATE KEY", text, StringComparison.Ordinal);
        }

        var previous = (await CallAsync(PkiTools.CaFile, """{"previous_id":"gone","format":"der"}"""))["file"]!;
        Assert.Equal(PkiMethods.ExportPrevious, previous["method"]!.GetValue<string>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.OpenDownloadAsync(admin, previous["method"]!.GetValue<string>(), previous["payload_json"]!.GetValue<string>(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.OpenDownloadAsync(admin, PkiMethods.Backup, "{}", CancellationToken.None)); // never a key file
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
