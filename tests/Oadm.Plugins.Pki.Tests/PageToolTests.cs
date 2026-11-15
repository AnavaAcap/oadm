using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;

using Oadm.Plugins.Pki.Ca;
using Oadm.Plugins.Pki.Policies;
using Oadm.Plugins.Pki.Tools;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// The page's actions of PKI as tools (docs/spec/mcp.md "Page actions", decision 2026-11-15): Create, Import, Delete, Back up and
/// Install in the server's trust of a CA, and the policies' Save and Delete. The preview writes nothing, the run does it once
/// with the page's checks; passwords only through the secret channel's payload of the run; the key backup only as a one-time file.
/// </summary>
public sealed class PageToolTests
{
    private static readonly FakeCoreContext Calls = new();

    private static async Task<JsonNode> CallAsync(PkiHarness pki, ICallContext caller, string method, object? payload = null)
    {
        var json = payload switch
        {
            null => null,
            string text => text,
            JsonNode node => node.ToJsonString(),
            _ => System.Text.Json.JsonSerializer.Serialize(payload, PkiTools.Json),
        };
        return JsonNode.Parse((await ((ICorePlugin)pki.Plugin).InvokeAsync(caller, method, json, CancellationToken.None))!)!;
    }

    private static string FieldOf(JsonNode answer) => Assert.Single(answer["errors"]!.AsArray())!["field"]!.GetValue<string>();

    private static string RowOf(PkiHarness pki, string authorityId) =>
        "ca:" + Convert.ToHexString(SHA256.HashData(pki.Service.Authorities().Single(a => a.Id == authorityId).Certificates[0]));

    [Fact]
    public void The_tools_have_the_rights_of_their_page_methods_and_audit_like_the_page()
    {
        ICorePlugin core = new PkiPlugin();
        var names = core.Tools.Select(t => t.Name).ToList();
        Assert.Equal(["all_policies", "ca_create", "ca_import", "ca_delete", "ca_key_backup", "server_trust_install", "policy_save", "policy_delete"],
            names.Except(["certificates", "policies", "certificate_create", "certificate_file", "ca_file"]));
        Assert.Equal(UserRole.Operator, core.RequiredRole("tool.all_policies"));
        Assert.False(core.RequiresAllDevices("tool.all_policies"));
        foreach (var tool in PkiPageTools.Set.Tools.Where(t => t.Changes))
        {
            Assert.Equal(UserRole.Admin, core.RequiredRole(tool.PreviewMethod));
            Assert.Equal(UserRole.Admin, core.RequiredRole(tool.RunMethod));
            Assert.True(core.RequiresAllDevices(tool.PreviewMethod));
            Assert.False(core.IsAudited(tool.PreviewMethod)); // a preview only checks
            Assert.True(core.IsAudited(tool.RunMethod));
            Assert.Equal(PkiPageTools.Set.PageMethodOf(tool.RunMethod), core.AuditDetail(tool.RunMethod, "{}")); // the page's own audit entry
        }

        Assert.Equal(PkiPermissions.TrustServer, core.RequiredPermission("tool.server_trust_install"));
        Assert.Equal(PkiPermissions.TrustServer, core.RequiredPermission("tool.server_trust_install.preview"));
        Assert.Null(core.RequiredPermission("tool.ca_create")); // "Manage the certificate authority", like Create CA
        Assert.True(PkiPageTools.Set.ToolOf("tool.ca_delete")!.Destructive);
        Assert.True(PkiPageTools.Set.ToolOf("tool.policy_delete")!.Destructive);
        Assert.Equal(["password"], PkiPageTools.Set.ToolOf("tool.ca_import")!.Secrets.Select(s => s.Name));
        Assert.Equal(["password"], PkiPageTools.Set.ToolOf("tool.ca_key_backup")!.Secrets.Select(s => s.Name));
        Assert.True(PkiPageTools.Set.ToolOf("tool.ca_key_backup")!.ReturnsFile);
    }

    [Fact]
    public async Task Create_ca_previews_without_writing_runs_once_and_answers_the_pages_field_errors()
    {
        await using var pki = await PkiHarness.StartAsync();
        var admin = Calls.CallAsAllDevices();
        var before = pki.Service.Authorities().Count;

        var plan = await CallAsync(pki, admin, "tool.ca_create.preview", new JsonObject { ["name"] = "Contoso Root CA", ["key"] = "ecc", ["validity_years"] = 5 });
        Assert.Equal(("Contoso Root CA", "root", "ECC P-256", 1, "certificate authority"),
            (plan["name"]!.GetValue<string>(), plan["type"]!.GetValue<string>(), plan["key"]!.GetValue<string>(), plan["will_change"]!.GetValue<int>(), plan["unit"]!.GetValue<string>()));
        Assert.Equal(before, pki.Service.Authorities().Count); // nothing made

        Assert.Equal("name", FieldOf(await CallAsync(pki, admin, "tool.ca_create.preview", new JsonObject { ["name"] = " " })));
        Assert.Equal("signed_by", FieldOf(await CallAsync(pki, admin, "tool.ca_create.preview", new JsonObject { ["name"] = "X", ["signed_by"] = "ca:00" })));
        Assert.Equal("validity_years", FieldOf(await CallAsync(pki, admin, "tool.ca_create.preview", new JsonObject { ["name"] = "X", ["validity_years"] = 31 })));

        var done = await CallAsync(pki, admin, "tool.ca_create", new JsonObject { ["name"] = "Contoso Root CA", ["key"] = "ecc", ["validity_years"] = 5 });
        Assert.Equal("Contoso Root CA", done["name"]!.GetValue<string>());
        Assert.Equal(before + 1, pki.Service.Authorities().Count);
        var root = pki.Service.Authorities().Single(a => a.Name == "Contoso Root CA");

        // An intermediate signed by the new root, named by its row of the certificate list.
        var signed = await CallAsync(pki, admin, "tool.ca_create.preview", new JsonObject { ["name"] = "Contoso Issuing CA", ["signed_by"] = RowOf(pki, root.Id) });
        Assert.Equal(("intermediate", "Contoso Root CA"), (signed["type"]!.GetValue<string>(), signed["signed_by"]!.GetValue<string>()));
        Assert.Equal("name", FieldOf(await CallAsync(pki, admin, "tool.ca_create.preview", new JsonObject { ["name"] = "contoso root ca" }))); // the name is taken
    }

    [Fact]
    public async Task Import_ca_asks_for_the_files_password_through_the_secret_and_adds_the_ca_only_with_the_run()
    {
        await using var pki = await PkiHarness.StartAsync();
        var uploads = new FakeUploadedFiles();
        var admin = Calls.CallAsAllDevices();
        admin.Uploads = uploads;
        using var root = TestCa.Root();
        using var intermediate = TestCa.Intermediate(root);
        using var rootPublic = X509CertificateLoader.LoadCertificate(root.RawData);
        var pfx = uploads.Add("acme.pfx", TestCa.Pfx("canary-pfx-1", intermediate, rootPublic));
        var before = pki.Service.Authorities().Count;

        var plan = await CallAsync(pki, admin, "tool.ca_import.preview", new JsonObject { ["file_id"] = pfx.Id });
        Assert.Equal(["password"], plan["needs_secrets"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(before, pki.Service.Authorities().Count);
        Assert.Equal("file_id", FieldOf(await CallAsync(pki, admin, "tool.ca_import.preview", new JsonObject { ["file_id"] = "0123456789abcdef0123456789abcdef" })));
        Assert.Equal("file_id", FieldOf(await CallAsync(pki, Calls.CallAsAllDevices(), "tool.ca_import.preview", new JsonObject { ["file_id"] = pfx.Id }))); // another session's upload

        var wrong = await CallAsync(pki, admin, "tool.ca_import", new JsonObject { ["file_id"] = pfx.Id, ["secrets"] = new JsonObject { ["password"] = "wrong" } });
        Assert.Equal("secrets.password", FieldOf(wrong));
        Assert.Equal(before, pki.Service.Authorities().Count);

        var done = await CallAsync(pki, admin, "tool.ca_import", new JsonObject { ["file_id"] = pfx.Id, ["secrets"] = new JsonObject { ["password"] = "canary-pfx-1" } });
        Assert.Equal("Acme Issuing CA", done["name"]!.GetValue<string>());
        Assert.DoesNotContain("canary-pfx-1", done.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(before + 1, pki.Service.Authorities().Count);

        // The same CA again: the page's refusal already in the preview.
        var again = await CallAsync(pki, admin, "tool.ca_import.preview", new JsonObject { ["file_id"] = pfx.Id });
        Assert.Equal(["password"], again["needs_secrets"]!.AsArray().Select(n => n!.GetValue<string>())); // the preview cannot open the file yet

        // A PEM file with its key and no password: the preview names the CA and needs no secret.
        using var other = TestCa.Root("Fabrikam Root CA");
        var pem = uploads.Add("fabrikam.pem", TestCa.Ascii(other.ExportCertificatePem() + "\n" + other.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem()));
        var plain = await CallAsync(pki, admin, "tool.ca_import.preview", new JsonObject { ["file_id"] = pem.Id });
        Assert.Equal("Fabrikam Root CA", plain["name"]!.GetValue<string>());
        Assert.Empty(plain["needs_secrets"]!.AsArray());
        Assert.Equal(before + 1, pki.Service.Authorities().Count);
    }

    [Fact]
    public async Task Delete_ca_is_refused_while_a_policy_names_it_and_deletes_only_with_the_run()
    {
        await using var pki = await PkiHarness.StartAsync();
        var admin = Calls.CallAsAllDevices();
        await CallAsync(pki, admin, "tool.ca_create", new JsonObject { ["name"] = "Spare CA", ["key"] = "ecc" });
        var spare = pki.Service.Authorities().Single(a => a.Name == "Spare CA");

        // The first-start root CA issues for the default policies: refused with the page's text.
        var used = await Assert.ThrowsAsync<InvalidOperationException>(() => CallAsync(pki, admin, "tool.ca_delete.preview", new JsonObject { ["ca_id"] = RowOf(pki, CertificateAuthorityInfo.OadmId) }));
        Assert.StartsWith("Used by the policy ", used.Message, StringComparison.Ordinal);
        Assert.Equal("ca_id", FieldOf(await CallAsync(pki, admin, "tool.ca_delete.preview", new JsonObject { ["ca_id"] = "ca:00" })));

        var plan = await CallAsync(pki, admin, "tool.ca_delete.preview", new JsonObject { ["ca_id"] = RowOf(pki, spare.Id) });
        Assert.Equal("Spare CA", plan["name"]!.GetValue<string>());
        Assert.Contains(pki.Service.Authorities(), a => a.Id == spare.Id); // nothing deleted

        await CallAsync(pki, admin, "tool.ca_delete", new JsonObject { ["ca_id"] = RowOf(pki, spare.Id) });
        Assert.DoesNotContain(pki.Service.Authorities(), a => a.Id == spare.Id);
    }

    [Fact]
    public async Task Back_up_ca_key_hands_out_the_pkcs12_once_to_its_caller_with_the_password_of_the_secret()
    {
        await using var pki = await PkiHarness.StartAsync();
        var admin = Calls.CallAsAllDevices();
        var row = RowOf(pki, CertificateAuthorityInfo.OadmId);

        var plan = await CallAsync(pki, admin, "tool.ca_key_backup.preview", new JsonObject { ["ca_id"] = row });
        Assert.NotEmpty(plan["warnings"]!.AsArray());
        Assert.Equal(0, pki.Plugin.Files.Count);
        Assert.Equal("ca_id", FieldOf(await CallAsync(pki, admin, "tool.ca_key_backup.preview", new JsonObject { ["ca_id"] = "ca:00" })));

        var shortPassword = await CallAsync(pki, admin, "tool.ca_key_backup", new JsonObject { ["ca_id"] = row, ["secrets"] = new JsonObject { ["password"] = "short" } });
        Assert.Equal("secrets.password", FieldOf(shortPassword));

        var done = await CallAsync(pki, admin, "tool.ca_key_backup", new JsonObject { ["ca_id"] = row, ["secrets"] = new JsonObject { ["password"] = "canary-backup-1" } });
        Assert.DoesNotContain("canary-backup-1", done.ToJsonString(), StringComparison.Ordinal);
        var file = done["file"]!;
        Assert.Equal(ToolFileStore.Method, file["method"]!.GetValue<string>());
        Assert.Equal("application/x-pkcs12", file["content_type"]!.GetValue<string>());
        var payload = file["payload_json"]!.GetValue<string>();
        Assert.DoesNotContain("canary-backup-1", payload, StringComparison.Ordinal);

        await Assert.ThrowsAsync<InvalidOperationException>(() => pki.Plugin.OpenDownloadAsync(Calls.CallAsAllDevices(), ToolFileStore.Method, payload, CancellationToken.None)); // another session
        await using (var download = await pki.Plugin.OpenDownloadAsync(admin, ToolFileStore.Method, payload, CancellationToken.None))
        {
            using var copy = new MemoryStream();
            await download.Content.CopyToAsync(copy);
            using var restored = X509CertificateLoader.LoadPkcs12(copy.ToArray(), "canary-backup-1", X509KeyStorageFlags.EphemeralKeySet);
            Assert.True(restored.HasPrivateKey);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => pki.Plugin.OpenDownloadAsync(admin, ToolFileStore.Method, payload, CancellationToken.None)); // once
    }

    [Fact]
    public async Task Install_in_the_server_trust_takes_a_root_ca_and_installs_only_with_the_run()
    {
        await using var pki = await PkiHarness.StartAsync();
        var admin = Calls.CallAsAllDevices();
        var row = RowOf(pki, CertificateAuthorityInfo.OadmId);
        var plan = await CallAsync(pki, admin, "tool.server_trust_install.preview", new JsonObject { ["ca_id"] = row });
        Assert.NotEmpty(plan["warnings"]!.AsArray());
        Assert.Equal(0, pki.Trust.Installs);

        var done = await CallAsync(pki, admin, "tool.server_trust_install", new JsonObject { ["ca_id"] = row });
        Assert.True(done["installed"]!.GetValue<bool>());
        Assert.Equal(1, pki.Trust.Installs);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CallAsync(pki, admin, "tool.server_trust_install.preview", new JsonObject { ["ca_id"] = row }));

        await CallAsync(pki, admin, "tool.ca_create", new JsonObject { ["name"] = "Root", ["key"] = "ecc" });
        await CallAsync(pki, admin, "tool.ca_create", new JsonObject { ["name"] = "Issuing", ["key"] = "ecc", ["signed_by"] = RowOf(pki, pki.Service.Authorities().Single(a => a.Name == "Root").Id) });
        var issuing = pki.Service.Authorities().Single(a => a.Name == "Issuing").Id;
        Assert.Equal("ca_id", FieldOf(await CallAsync(pki, admin, "tool.server_trust_install.preview", new JsonObject { ["ca_id"] = RowOf(pki, issuing) })));
    }

    [Fact]
    public async Task A_policy_is_saved_and_deleted_only_by_the_run_with_the_pages_checks_and_warnings()
    {
        await using var pki = await PkiHarness.StartAsync();
        var admin = Calls.CallAsAllDevices();
        var count = pki.Policies.All.Count;

        var plan = await CallAsync(pki, admin, "tool.policy_save.preview", new JsonObject { ["kind"] = PolicyKinds.Block, ["name"] = "Lab", ["devices"] = new JsonObject { ["tags"] = new JsonArray("Lab") } });
        Assert.Equal(("create", "Block"), (plan["change"]!.GetValue<string>(), plan["kind"]!.GetValue<string>()));
        Assert.Contains("lose the network", plan["warnings"]![0]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(count, pki.Policies.All.Count);
        Assert.Equal("kind", FieldOf(await CallAsync(pki, admin, "tool.policy_save.preview", new JsonObject { ["kind"] = "nothing", ["name"] = "Lab" })));
        Assert.Equal("vlan", FieldOf(await CallAsync(pki, admin, "tool.policy_save.preview", new JsonObject { ["kind"] = PolicyKinds.Vlan, ["name"] = "V", ["vlan"] = 5000 })));

        var saved = await CallAsync(pki, admin, "tool.policy_save", new JsonObject { ["kind"] = PolicyKinds.Block, ["name"] = "Lab", ["devices"] = new JsonObject { ["tags"] = new JsonArray("Lab") } });
        var id = saved["id"]!.GetValue<string>();
        Assert.Equal(count + 1, pki.Policies.All.Count);
        Assert.Equal("name", FieldOf(await CallAsync(pki, admin, "tool.policy_save.preview", new JsonObject { ["kind"] = PolicyKinds.Block, ["name"] = "lab", ["devices"] = new JsonObject { ["tags"] = new JsonArray("Other") } })));

        var listed = await CallAsync(pki, admin, "tool.all_policies");
        Assert.Contains(listed["policies"]!.AsArray(), p => p!["id"]!.GetValue<string>() == id && p["kind"]!.GetValue<string>() == PolicyKinds.Block);
        Assert.Empty((await CallAsync(pki, Calls.CallAsGroup([]), "tool.all_policies"))["policies"]!.AsArray()); // server-wide: a group sees none

        var renamed = await CallAsync(pki, admin, "tool.policy_save", new JsonObject { ["policy_id"] = id, ["name"] = "Lab B" });
        Assert.Equal("Lab B", renamed["name"]!.GetValue<string>());
        Assert.Equal(["Lab"], pki.Policies.Find(Guid.Parse(id))!.Devices!.Tags); // fields left out keep their value

        var delete = await CallAsync(pki, admin, "tool.policy_delete.preview", new JsonObject { ["policy_id"] = id });
        Assert.Equal("Lab B", delete["name"]!.GetValue<string>());
        Assert.Equal(count + 1, pki.Policies.All.Count);
        await CallAsync(pki, admin, "tool.policy_delete", new JsonObject { ["policy_id"] = id });
        Assert.Equal(count, pki.Policies.All.Count);
        Assert.Equal("policy_id", FieldOf(await CallAsync(pki, admin, "tool.policy_delete.preview", new JsonObject { ["policy_id"] = id })));

        // A system policy is never deleted: the page's refusal.
        var system = pki.Policies.FirstOf(PolicyKinds.HttpsCertificate)!;
        Assert.Equal("name", FieldOf(await CallAsync(pki, admin, "tool.policy_delete.preview", new JsonObject { ["policy_id"] = system.Id.ToString() })));
    }

    [Fact]
    public async Task Without_oadm_pro_a_pro_kind_is_refused()
    {
        await using var pki = await PkiHarness.StartAsync(pro: false);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => CallAsync(pki, Calls.CallAsAllDevices(), "tool.policy_save.preview",
            new JsonObject { ["kind"] = PolicyKinds.Vlan, ["name"] = "V", ["vlan"] = 30, ["devices"] = new JsonObject { ["tags"] = new JsonArray("Lab") } }));
        Assert.Equal(Oadm.Sdk.Licensing.ProLicenseTexts.NeedsPro, refused.Message);
    }
}
