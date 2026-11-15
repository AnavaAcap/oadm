using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Core.Auth;
using Oadm.Core.Plugins;
using Oadm.Plugins.VapixCommander;
using Oadm.Sdk;
using Oadm.Sdk.Plugins;
using Oadm.Server.Plugins;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Mcp;

/// <summary>
/// The secrets of a plugin tool's change (docs/spec/mcp.md "Secrets", "Page actions"): VAPIX Commander's saved command with a
/// password named in set_passwords. The preview's plan names the slot (<see cref="PluginToolPlan.SecretsNeeded"/>), the host
/// declares it with its link, checks an entered value with the plugin's preview, and the run stores it encrypted like the
/// page; the value is in no answer, audit entry, log line, task or file of the data folder in clear. Only a role with the
/// page's right and all devices gets the tool.
/// </summary>
public sealed class McpCommanderSecretTests
{
    private const string Canary = "canary-plugin-secret-6Vn";

    private static async Task<(McpTestServer Server, VapixCommanderPlugin Plugin)> StartAsync()
    {
        var server = await McpTestServer.StartAsync();
        var registry = server.Host.Get<PluginRegistry>();
        var plugin = new VapixCommanderPlugin();
        registry.RegisterCorePlugin(plugin, new PluginOrigin(VapixCommanderPlugin.PluginId, "1.0.0", null) { DisplayName = "VAPIX Commander", MinSdkVersion = SdkInfo.Version });
        await server.Host.Get<PluginActivation>().LoadAsync(CancellationToken.None);
        await server.Host.Get<RoleStore>().SyncCatalogAsync(PermissionCatalog.Build(registry), CancellationToken.None);
        await server.Host.Get<CorePluginHost>().StartAllAsync(CancellationToken.None);
        return (server, plugin);
    }

    private static async Task<HttpResponseMessage> PutAsync(McpTestServer server, string key, string url, string value)
    {
        using var client = server.Http();
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(url).PathAndQuery) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(value)) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var response = await client.SendAsync(request);
        await response.Content.LoadIntoBufferAsync();
        return response;
    }

    private static string CommandJson(VapixCommanderPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var command = new CommandDefinition
        {
            Name = "Brand with a password",
            Requires = [new ApiRequirement { Api = "param-cgi", MinVersion = "1.0" }],
            Fields = [new CommandField { Name = "pass", Label = "Admin password", Type = FieldTypes.Password, Required = false }],
            Request = new CommandRequest { Method = "GET", Path = "/axis-cgi/param.cgi", Query = new Dictionary<string, string> { ["action"] = "list", ["group"] = "Brand" } },
            Response = new CommandResponse { Kind = ResponseKinds.ParamCgi },
        };
        return JsonSerializer.Serialize(command, Oadm.Plugins.VapixCommander.CommandJson.Strict);
    }

    [Fact]
    public async Task APasswordOfASavedCommandGoesThroughItsLinkIsCheckedAndIsStoredOnlyEncrypted()
    {
        var (server, plugin) = await StartAsync();
        await using var _ = server;
        var key = await server.CreateKeyAsync(name: "Cmd-bot");
        var answers = new StringBuilder();

        var input = new JsonObject { ["command"] = CommandJson(plugin), ["set_passwords"] = new JsonArray("pass") };
        var preview = await server.ToolAsync(key, "oadm_vapix_saved_save_preview", input);
        answers.Append(preview.ToJsonString());
        Assert.Null(preview["secrets_needed"]); // the host takes it out
        Assert.Equal("pass", Assert.Single(preview["needs_secrets"]!.AsArray())!.GetValue<string>());
        Assert.Contains("Admin password: ", preview["secret_links"]!["pass"]!["command"]!.GetValue<string>(), StringComparison.Ordinal);
        var url = preview["secret_links"]!["pass"]!["url"]!.GetValue<string>();
        var handle = preview["handle"]!.GetValue<string>();

        var early = await server.CallToolAsync(key, "oadm_vapix_saved_save_run", new JsonObject { ["handle"] = handle });
        Assert.Contains("Enter the admin password.", early.ToJsonString(), StringComparison.Ordinal);
        Assert.Empty(await plugin.SavedStore.ListAsync(CancellationToken.None));

        // The plugin's own check refuses a value its field does not take; the link stays.
        using (var bad = await PutAsync(server, key, url, "with\0nul"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }

        using (var set = await PutAsync(server, key, url, Canary))
        {
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
            answers.Append(await set.Content.ReadAsStringAsync());
        }

        var run = await server.ToolAsync(key, "oadm_vapix_saved_save_run", new JsonObject { ["handle"] = handle });
        answers.Append(run.ToJsonString());
        var id = run["id"]!.GetValue<string>();
        Assert.Equal(Canary, (await plugin.SavedStore.GetSecretsAsync(id, CancellationToken.None))["pass"]);
        answers.Append((await server.ToolAsync(key, "oadm_vapix_saved_export", new JsonObject())).ToJsonString());

        var audit = (await server.Host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        Assert.Contains(audit, e => e.Action == AuditActions.SecretSet && e.Target == "admin password" && e.UserName == AccessKeyStore.AgentNameOf("Cmd-bot"));
        await SecretScan.AssertNowhereAsync(server.Host, Canary, answers.ToString());
    }

    [Fact]
    public async Task AnAgentCannotSendThePasswordAndARoleWithoutThePagesRightOrAllDevicesGetsNoTool()
    {
        var (server, plugin) = await StartAsync();
        await using var _ = server;
        var key = await server.CreateKeyAsync();

        // The secrets property is the host's: an agent's own one is refused by the strict schema.
        var sent = await server.CallToolAsync(key, "oadm_vapix_saved_save_preview", new JsonObject { ["command"] = CommandJson(plugin), ["secrets"] = new JsonObject { ["pass"] = Canary } });
        Assert.True(sent["isError"]!.GetValue<bool>());

        var viewer = await server.CreateKeyAsync(name: "viewer", roleId: BuiltInRoles.ViewerId.ToString());
        var group = await GroupCallers.GroupAsync(server.Host, "Lobby group", new Proto.DeviceGroupRules());
        var grouped = await server.CreateKeyAsync(name: "grouped", roleId: BuiltInRoles.OperatorId.ToString(), groupId: group.ToString());
        foreach (var other in new[] { viewer, grouped })
        {
            var list = await server.PostAsync(other, McpTestServer.Request("tools/list"));
            Assert.DoesNotContain("oadm_vapix_saved_save_preview", list.Body!.ToJsonString(), StringComparison.Ordinal);
        }
    }
}
