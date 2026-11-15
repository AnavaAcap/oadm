using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;

using Oadm.Core.Auth;
using Oadm.Core.Persistence;
using Oadm.Core.Plugins;
using Oadm.Plugins.Pki;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Plugins;
using Oadm.Server.Plugins;
using Oadm.Server.Tools;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Mcp;

/// <summary>
/// The page actions of PKI, the EST server and the 802.1X server over MCP (docs/spec/mcp.md "Page actions", decision 2026-11-15):
/// a preview that declares a secret answers its one-time link, the run gets the value once, a key backup and a generated shared
/// secret come only as a one-time file over a download link, and no answer, audit entry or log line holds the secret.
/// </summary>
public sealed class McpPkiPageToolTests
{
    private const string Canary = "canary-backup-7d2f";

    public static readonly string[] ToolNames =
    [
        "oadm_pki_all_policies", "oadm_pki_ca_create_preview", "oadm_pki_ca_create_run", "oadm_pki_ca_import_preview", "oadm_pki_ca_import_run",
        "oadm_pki_ca_delete_preview", "oadm_pki_ca_delete_run", "oadm_pki_ca_key_backup_preview", "oadm_pki_ca_key_backup_run",
        "oadm_pki_server_trust_install_preview", "oadm_pki_server_trust_install_run", "oadm_pki_policy_save_preview", "oadm_pki_policy_save_run",
        "oadm_pki_policy_delete_preview", "oadm_pki_policy_delete_run",
        "oadm_est_enable_preview", "oadm_est_enable_run", "oadm_est_settings_save_preview", "oadm_est_settings_save_run",
        "oadm_est_profile_save_preview", "oadm_est_profile_save_run", "oadm_est_profile_delete_preview", "oadm_est_profile_delete_run",
        "oadm_radius_switches", "oadm_radius_enable_preview", "oadm_radius_enable_run", "oadm_radius_settings_save_preview", "oadm_radius_settings_save_run",
        "oadm_radius_switch_save_preview", "oadm_radius_switch_save_run", "oadm_radius_switch_delete_preview", "oadm_radius_switch_delete_run",
    ];

    private static async Task<McpTestServer> StartAsync()
    {
        var server = await McpTestServer.StartAsync();
        var registry = server.Host.Get<PluginRegistry>();
        var pki = new PkiPlugin(new PkiOptions { CaKeySize = 2048, ServerName = "SERVER01", IssuedFlushDelay = TimeSpan.Zero, CertificatesEventDelay = TimeSpan.Zero });
        registry.RegisterCorePlugin(pki, new PluginOrigin("oadm.pki", "1.0.0", null) { DisplayName = "PKI" });
        registry.RegisterCorePlugin(new Oadm.Plugins.Est.EstPlugin(), new PluginOrigin(Oadm.Plugins.Est.EstPluginInfo.PluginId, "1.0.0", null) { DisplayName = "EST server" });
        registry.RegisterCorePlugin(new Oadm.Plugins.Radius.RadiusPlugin(), new PluginOrigin(Oadm.Plugins.Radius.RadiusPlugin.PluginId, "1.0.0", null) { DisplayName = "802.1X server" });
        await server.Host.Get<PluginActivation>().LoadAsync(CancellationToken.None);
        await server.Host.Get<RoleStore>().SyncCatalogAsync(PermissionCatalog.Build(registry), CancellationToken.None);
        await server.Host.Get<CorePluginHost>().StartAllAsync(CancellationToken.None);
        await pki.Service!.Background; // the default CA
        server.Host.Get<PluginServiceRegistry>().For("oadm.pki").Offer<IServerCertificates>(server.Certificates);
        await server.Listener.ApplyAsync(CancellationToken.None);
        Assert.NotNull(server.Listener.App);
        return server;
    }

    private static async Task<HttpResponseMessage> SendAsync(McpTestServer server, HttpMethod method, string key, string url, byte[]? body = null)
    {
        using var client = server.Http();
        using var request = new HttpRequestMessage(method, new Uri(url).PathAndQuery);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var response = await client.SendAsync(request);
        await response.Content.LoadIntoBufferAsync();
        return response;
    }

    [Fact]
    public async Task AnAdministratorListsThePageActionsAndAViewerNoneOfTheChanges()
    {
        await using var server = await StartAsync();
        var key = await server.CreateKeyAsync();
        var names = await server.ToolNamesAsync(key);
        Assert.All(ToolNames, name => Assert.Contains(name, names));

        var saved = await new Proto.RoleService.RoleServiceClient(server.Host.Invoker).SaveRolesAsync(new Proto.SaveRolesRequest { Roles = { new Proto.RoleChange { Name = "Lookers" } } });
        var viewer = await server.CreateKeyAsync(name: "looker", roleId: saved.Roles.Single(r => r.Name == "Lookers").Id);
        var viewerNames = await server.ToolNamesAsync(viewer);
        Assert.All(ToolNames.Where(n => n.EndsWith("_preview", StringComparison.Ordinal) || n.EndsWith("_run", StringComparison.Ordinal)), name => Assert.DoesNotContain(name, viewerNames));
    }

    [Fact]
    public async Task AKeyBackupTakesItsPasswordThroughTheSecretLinkAndComesOnceOverADownloadLink()
    {
        await using var server = await StartAsync();
        var key = await server.CreateKeyAsync(name: "Backup-bot");
        var answers = new StringBuilder();
        var certificates = await server.ToolAsync(key, "oadm_pki_certificates", new JsonObject { ["purpose"] = "ca", ["limit"] = 200 });
        var row = certificates["certificates"]!.AsArray().First(c => c!["kind"]!.GetValue<string>() == "Authority")!["id"]!.GetValue<string>();

        var preview = await server.ToolAsync(key, "oadm_pki_ca_key_backup_preview", new JsonObject { ["ca_id"] = row });
        answers.Append(preview.ToJsonString());
        Assert.Equal("password", Assert.Single(preview["needs_secrets"]!.AsArray())!.GetValue<string>());
        var url = preview["secret_links"]!["password"]!["url"]!.GetValue<string>();
        var handle = preview["handle"]!.GetValue<string>();

        var early = await server.CallToolAsync(key, "oadm_pki_ca_key_backup_run", new JsonObject { ["handle"] = handle });
        Assert.True(early["isError"]!.GetValue<bool>());
        Assert.Contains("Enter the backup password.", early.ToJsonString(), StringComparison.Ordinal);

        using (var set = await SendAsync(server, HttpMethod.Put, key, url, Encoding.UTF8.GetBytes(Canary + "\n")))
        {
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        }

        var run = await server.ToolAsync(key, "oadm_pki_ca_key_backup_run", new JsonObject { ["handle"] = handle });
        answers.Append(run.ToJsonString());
        var file = run["file"]!;
        Assert.Null(file["payload_json"]); // a reference, never the plugin's payload
        var link = await server.ToolAsync(key, ToolFiles.LinkTool, new JsonObject { ["file_ref"] = file["file_ref"]!.GetValue<string>() });
        answers.Append(link.ToJsonString());
        using (var fetched = await SendAsync(server, HttpMethod.Get, key, link["url"]!.GetValue<string>()))
        {
            Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
            using var restored = X509CertificateLoader.LoadPkcs12(await fetched.Content.ReadAsByteArrayAsync(), Canary, X509KeyStorageFlags.EphemeralKeySet);
            Assert.True(restored.HasPrivateKey);
        }

        // The file is made once: a second link of the same reference finds nothing.
        var again = await server.ToolAsync(key, ToolFiles.LinkTool, new JsonObject { ["file_ref"] = file["file_ref"]!.GetValue<string>() });
        using (var twice = await SendAsync(server, HttpMethod.Get, key, again["url"]!.GetValue<string>()))
        {
            Assert.NotEqual(HttpStatusCode.OK, twice.StatusCode);
        }

        var audit = (await server.Host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        Assert.Contains(audit, e => e.Action == AuditActions.PluginCall && e.Detail.Contains(PkiMethods.Backup, StringComparison.Ordinal) && e.UserName == AccessKeyStore.AgentNameOf("Backup-bot"));
        Assert.Contains(audit, e => e.Action == AuditActions.SecretSet && e.Target == "backup password");
        var logs = McpSecretTests.ReadLogs(server.Host.Get<OadmPaths>().LogsDirectory);
        Assert.DoesNotContain(Canary, answers.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(audit, e => string.Join('|', e.Target, e.Detail).Contains(Canary, StringComparison.Ordinal));
        Assert.DoesNotContain(Canary, logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACaIsCreatedOnlyByItsRunAndAPolicyThatNamesACaKeepsIt()
    {
        await using var server = await StartAsync();
        var key = await server.CreateKeyAsync();
        var before = (await server.ToolAsync(key, "oadm_pki_certificates", new JsonObject { ["purpose"] = "ca" }))["total"]!.GetValue<int>();
        var preview = await server.ToolAsync(key, "oadm_pki_ca_create_preview", new JsonObject { ["name"] = "Contoso Root CA", ["key"] = "ecc" });
        Assert.Equal("root", preview["type"]!.GetValue<string>());
        Assert.Equal(before, (await server.ToolAsync(key, "oadm_pki_certificates", new JsonObject { ["purpose"] = "ca" }))["total"]!.GetValue<int>());
        await server.ToolAsync(key, "oadm_pki_ca_create_run", new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() });
        Assert.Equal(before + 1, (await server.ToolAsync(key, "oadm_pki_certificates", new JsonObject { ["purpose"] = "ca" }))["total"]!.GetValue<int>());
        var once = await server.CallToolAsync(key, "oadm_pki_ca_create_run", new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() });
        Assert.True(once["isError"]!.GetValue<bool>());

        var first = (await server.ToolAsync(key, "oadm_pki_certificates", new JsonObject { ["purpose"] = "ca", ["search"] = "OADM Root CA" }))["certificates"]![0]!["id"]!.GetValue<string>();
        var used = await server.CallToolAsync(key, "oadm_pki_ca_delete_preview", new JsonObject { ["ca_id"] = first });
        Assert.True(used["isError"]!.GetValue<bool>());
        Assert.Contains("Used by the policy", used.ToJsonString(), StringComparison.Ordinal);
    }
}
