using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;

using Oadm.Core.Auth;
using Oadm.Core.Plugins;
using Oadm.Plugins.Users;
using Oadm.Sdk.Testing;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Mcp;

/// <summary>
/// The secrets of Users and PKI over MCP (docs/spec/mcp.md "Secrets"): the Users task's password and the PKCS#12 password of
/// the PKI import go through their secret links only (labelled "password" and "PKCS#12 password"), a wrong PKCS#12 password is
/// refused when it is entered, the runs get the values once, and no canary reaches an answer, the audit log, the server log,
/// a task or the data folder. A group user lists none of the PKI changes, and its Users preview sees only its devices.
/// </summary>
public sealed class McpUsersPkiSecretTests
{
    private const string Canary = "canary-pfx-4Hd!w9 secret";

    private static async Task<HttpResponseMessage> PutAsync(McpTestServer server, string key, string url, byte[] body)
    {
        using var client = server.Http();
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(url).PathAndQuery) { Content = new ByteArrayContent(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var response = await client.SendAsync(request);
        await response.Content.LoadIntoBufferAsync();
        return response;
    }

    private static async Task<string> UploadAsync(McpTestServer server, string key, string name, byte[] file)
    {
        var link = await server.ToolAsync(key, "oadm_files_upload_link", new JsonObject { ["name"] = name, ["size"] = file.Length });
        using var sent = await PutAsync(server, key, link["url"]!.GetValue<string>(), file);
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        return JsonNode.Parse(await sent.Content.ReadAsStringAsync())!["file_id"]!.GetValue<string>();
    }

    /// <summary>A device with the user management API in its cached list (the Users task can run), with tags.</summary>
    private static async Task<Oadm.Core.Devices.Device> UsersDeviceAsync(TestServerHost host, int serial, params string[] tags)
    {
        var device = await host.Get<Oadm.Core.Devices.DeviceRepository>().AddAsync(new Oadm.Core.Devices.Device
        {
            Serial = FakeSerials.Make(serial),
            Address = "10.9.1." + serial,
            Scheme = Oadm.Core.Devices.DeviceScheme.Http,
            Status = Oadm.Sdk.Devices.DeviceStatus.Ok,
            FirmwareVersion = "12.8.0",
            Apis = [new Oadm.Sdk.Vapix.DeviceApi("user-management", "1.2")],
        }, CancellationToken.None);
        await host.Get<Oadm.Core.Security.CredentialStore>().SetAsync(device.Id, "root", "pw", CancellationToken.None);
        if (tags.Length > 0)
        {
            await host.Tags.SetDeviceTagsAsync(new Proto.SetDeviceTagsRequest { DeviceIds = { device.Id.ToString() }, Add = { tags } });
        }

        return device;
    }

    private static byte[] Pfx(string password)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Lobby door", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(300));
        return certificate.ExportPkcs12(Pkcs12ExportPbeParameters.Pbes2Aes256Sha256, password);
    }

    [Fact]
    public async Task APkcs12ImportTakesItsPasswordThroughTheLinkRefusesAWrongOneAndKeepsItNowhere()
    {
        await using var server = await McpPluginToolTests.StartBundledAsync();
        var key = await server.CreateKeyAsync(name: "Lobby-bot");
        var answers = new StringBuilder();
        var before = (await server.ToolAsync(key, "oadm_pki_certificates"))["total"]!.GetValue<int>();
        var fileId = await UploadAsync(server, key, "door.pfx", Pfx(Canary));

        var preview = await server.ToolAsync(key, "oadm_pki_certificate_import_preview", new JsonObject { ["file_id"] = fileId });
        answers.Append(preview.ToJsonString());
        Assert.Equal("password", Assert.Single(preview["needs_secrets"]!.AsArray())!.GetValue<string>());
        Assert.False(preview.ContainsKey("secrets_needed")); // the host takes the plugin's list out
        Assert.Contains("PKCS#12 password: ", preview["secret_links"]!["password"]!["command"]!.GetValue<string>(), StringComparison.Ordinal);
        var url = preview["secret_links"]!["password"]!["url"]!.GetValue<string>();

        using (var wrong = await PutAsync(server, key, url, Encoding.UTF8.GetBytes("not the password")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode); // the page's own check, at once; the link stays
            Assert.Contains("The password is wrong", await wrong.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using (var set = await PutAsync(server, key, url, Encoding.UTF8.GetBytes(Canary)))
        {
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
            answers.Append(await set.Content.ReadAsStringAsync());
        }

        Assert.Equal(before, (await server.ToolAsync(key, "oadm_pki_certificates"))["total"]!.GetValue<int>()); // nothing written yet
        var run = await server.ToolAsync(key, "oadm_pki_certificate_import_run", new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() });
        answers.Append(run.ToJsonString());
        Assert.Equal("Lobby door", run["name"]!.GetValue<string>());
        var listed = await server.ToolAsync(key, "oadm_pki_certificates", new JsonObject { ["search"] = "Lobby door" });
        answers.Append(listed.ToJsonString());
        var row = Assert.Single(listed["certificates"]!.AsArray())!;
        Assert.True(row["has_key"]!.GetValue<bool>());

        var audit = (await server.Host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        Assert.Contains(audit, e => e.Action == AuditActions.SecretSet && e.Target == "PKCS#12 password" && e.UserName == AccessKeyStore.AgentNameOf("Lobby-bot"));
        await SecretScan.AssertNowhereAsync(server.Host, Canary, answers.ToString());
    }

    [Fact]
    public async Task ACertificateWithoutAKeyNeedsNoSecretAndARequestIsSignedIntoAFile()
    {
        await using var server = await McpPluginToolTests.StartBundledAsync();
        var key = await server.CreateKeyAsync();

        // A certificate without a key: no secret, the run imports it.
        using var rsa = RSA.Create(2048);
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256); // the HTTPS policy's key kind
        using (var plain = new CertificateRequest("CN=Gate public", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30)))
        {
            var fileId = await UploadAsync(server, key, "gate.crt", Encoding.ASCII.GetBytes(plain.ExportCertificatePem()));
            var preview = await server.ToolAsync(key, "oadm_pki_certificate_import_preview", new JsonObject { ["file_id"] = fileId });
            Assert.False(preview.ContainsKey("needs_secrets") && preview["needs_secrets"] is JsonArray { Count: > 0 });
            Assert.Equal("Gate public", preview["name"]!.GetValue<string>());
            Assert.Equal("Gate public", (await server.ToolAsync(key, "oadm_pki_certificate_import_run", new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() }))["name"]!.GetValue<string>());
        }

        var unknown = await server.CallToolAsync(key, "oadm_pki_certificate_import_preview", new JsonObject { ["file_id"] = "nope" });
        Assert.True(unknown["isError"]!.GetValue<bool>());

        // A certificate request: its names come from the request, the run hands out the certificate as a file.
        var csr = new CertificateRequest("CN=gate.example", ec, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("gate.example");
        csr.CertificateExtensions.Add(san.Build());
        var sign = await server.ToolAsync(key, "oadm_pki_certificate_sign_preview", new JsonObject { ["request"] = csr.CreateSigningRequestPem().ReplaceLineEndings(" "), ["use"] = "https" });
        Assert.Equal("gate.example", sign["name"]!.GetValue<string>());
        var signed = await server.ToolAsync(key, "oadm_pki_certificate_sign_run", new JsonObject { ["handle"] = sign["handle"]!.GetValue<string>() });
        Assert.Equal("gate.example", signed["name"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(signed["file"]!["file_ref"]!.GetValue<string>()));
        Assert.DoesNotContain("PRIVATE KEY", signed.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyARoleThatManagesTheCaWithAllDevicesGetsTheImportAndTheSign()
    {
        await using var server = await McpPluginToolTests.StartBundledAsync();
        var roles = new Proto.RoleService.RoleServiceClient(server.Host.Invoker);
        var saved = await roles.SaveRolesAsync(new Proto.SaveRolesRequest { Roles = { new Proto.RoleChange { Name = "Viewers", Permissions = { Oadm.Sdk.Plugins.PermissionKeys.Of("oadm.pki", Oadm.Sdk.Plugins.PermissionKeys.Show) } } } });
        var viewer = await server.CreateKeyAsync(await server.Host.InvokerForRoleAsync("viewer1", Guid.Parse(saved.Roles.Single(r => r.Name == "Viewers").Id)));
        var group = await GroupCallers.ForTagAsync(server.Host, "tech", "Lobby");
        var grouped = await server.CreateKeyAsync(group.Invoker);
        foreach (var caller in new[] { viewer, grouped })
        {
            var tools = await server.ToolNamesAsync(caller);
            Assert.DoesNotContain("oadm_pki_certificate_import_preview", tools);
            Assert.DoesNotContain("oadm_pki_certificate_sign_preview", tools);
        }

        Assert.Contains("oadm_pki_certificate_import_preview", await server.ToolNamesAsync(await server.CreateKeyAsync()));
    }

    [Fact]
    public async Task AUsersPasswordGoesThroughItsLinkToTheRunAndAGroupUserPlansOnlyItsDevices()
    {
        await using var server = await McpTestServer.StartAsync();
        var registry = server.Host.Get<PluginRegistry>();
        Assert.True(registry.RegisterTaskPlugin(new UsersTaskPlugin(), new PluginOrigin(UsersTaskPlugin.PluginId, "1.0.0", null) { MinSdkVersion = Oadm.Sdk.Plugins.SdkInfo.Version }));
        await server.Host.Get<RoleStore>().SyncCatalogAsync(PermissionCatalog.Build(registry), CancellationToken.None);
        var inside = await UsersDeviceAsync(server.Host, 1, "Lobby");
        var outside = await UsersDeviceAsync(server.Host, 2);
        var group = await GroupCallers.ForTagAsync(server.Host, "tech", "Lobby");
        var key = await server.CreateKeyAsync(group.Invoker, name: "Lobby-bot");
        var answers = new StringBuilder();

        var catalog = await server.ToolAsync(key, "oadm_tasks_catalog", new JsonObject { ["task"] = UsersTaskPlugin.PluginId });
        Assert.DoesNotContain("\"password\"", catalog["tasks"]![0]!["input_schema"]!.ToJsonString(), StringComparison.Ordinal);

        // A removal needs no password.
        var remove = await server.ToolAsync(key, "oadm_task_preview", new JsonObject
        {
            ["task"] = UsersTaskPlugin.PluginId,
            ["filter"] = new JsonObject { ["search"] = "10.9.1." },
            ["input"] = new JsonObject { ["mode"] = "remove", ["userNames"] = new JsonArray("old") },
        });
        Assert.Empty(remove["needs_secrets"]?.AsArray() ?? []);

        var preview = await server.ToolAsync(key, "oadm_task_preview", new JsonObject
        {
            ["task"] = UsersTaskPlugin.PluginId,
            ["filter"] = new JsonObject { ["search"] = "10.9.1." },
            ["input"] = new JsonObject { ["mode"] = "add", ["userName"] = "joe", ["role"] = "operator" },
        });
        answers.Append(preview.ToJsonString());
        Assert.Equal(1, preview["devices"]!.GetValue<int>()); // the group's device only
        Assert.DoesNotContain(outside.Id.ToString(), preview.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains(preview["warnings"]!.AsArray(), w => w!.GetValue<string>() == "Adds user joe as Operator and sets its password.");
        Assert.Equal("password", Assert.Single(preview["needs_secrets"]!.AsArray())!.GetValue<string>());
        var url = preview["secret_links"]!["password"]!["url"]!.GetValue<string>();

        using (var accented = await PutAsync(server, key, url, Encoding.UTF8.GetBytes("pässword")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, accented.StatusCode); // the dialog's base rule, at once
        }

        using (var set = await PutAsync(server, key, url, Encoding.UTF8.GetBytes(Canary)))
        {
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        }

        var run = await server.ToolAsync(key, "oadm_task_run", new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() });
        answers.Append(run.ToJsonString());
        var batch = run["batch"]!.GetValue<string>();
        await Eventually.TrueAsync(async () => (await server.Host.Tasks.ListAsync(new Proto.ListTasksRequest())).Tasks
            .Any(t => t.BatchId == batch && t.State is Proto.TaskState.Done or Proto.TaskState.Failed), "the task ended");
        var listed = await server.ToolAsync(key, "oadm_tasks_list", new JsonObject { ["batch"] = batch });
        answers.Append(listed.ToJsonString());
        Assert.Equal("Add user joe", listed["tasks"]![0]!["name"]!.GetValue<string>());
        Assert.NotEqual(inside.Id, outside.Id);
        await SecretScan.AssertNowhereAsync(server.Host, Canary, answers.ToString());
    }
}
