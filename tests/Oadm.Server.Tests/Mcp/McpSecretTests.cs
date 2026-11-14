using System.Text;
using System.Text.Json.Nodes;

using Oadm.Core.Auth;
using Oadm.Core.Persistence;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Mcp;

/// <summary>
/// Rule 4 "No secrets" (docs/spec/mcp.md "Tests"): with known passwords, a credential list entry, a session token and the access
/// key itself in the server, a call of every tool, the refusals, the audit log and the server log hold none of them.
/// </summary>
public sealed class McpSecretTests
{
    private const string DevicePassword = "canary-device-6f1d";
    private const string ListPassword = "canary-list-93ac";

    [Fact]
    public async Task NoAnswerAuditEntryOrLogLineHoldsAPasswordATokenOrTheKey()
    {
        await using var server = await McpTestServer.StartAsync();
        var device = await DeviceServiceTests.AddDeviceAsync(server.Host, "10.4.0.1", 4001, password: DevicePassword);
        await server.Host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "root", Password = ListPassword });
        var key = await server.CreateKeyAsync(name: "canary");
        var answers = new StringBuilder();

        var calls = new Dictionary<string, JsonObject>
        {
            ["oadm_server_info"] = [],
            ["oadm_devices_summary"] = new() { ["group_by"] = "model" },
            ["oadm_devices_search"] = new() { ["fields"] = new JsonArray([.. Oadm.Server.Tools.DeviceTools.ExtraFields.Select(f => (JsonNode)f)]) },
            ["oadm_devices_get"] = new() { ["device_id"] = device.Id.ToString() },
            ["oadm_tasks_list"] = [],
            ["oadm_tasks_get"] = new() { ["task_id"] = Guid.NewGuid().ToString() },
            ["oadm_logs_search"] = new() { ["text"] = "MCP" },
            ["oadm_logs_audit"] = [],
            ["oadm_tasks_catalog"] = [],
            ["oadm_task_preview"] = new() { ["task"] = "oadm.restart", ["filter"] = new JsonObject(), ["input"] = new JsonObject() },
            ["oadm_task_run"] = new() { ["handle"] = "ended" },
            ["oadm_files_upload_link"] = new() { ["name"] = "a.bin", ["size"] = 1 },
        };
        Assert.Equal(calls.Keys.Order(StringComparer.Ordinal), await server.ToolNamesAsync(key));
        foreach (var (tool, arguments) in calls)
        {
            answers.Append((await server.CallToolAsync(key, tool, arguments)).ToJsonString());
        }

        answers.Append((await server.PostAsync(key, McpTestServer.Request("tools/list"))).Body?.ToJsonString());
        answers.Append((await server.PostAsync(key + "x", McpTestServer.Request("tools/list"))).Body?.ToJsonString());
        answers.Append((await server.PostAsync(server.Host.AdminToken, McpTestServer.Request("tools/list"))).Body?.ToJsonString());

        var audit = (await server.Host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries
            .Select(e => string.Join('|', e.UserName, e.Action, e.Target, e.Detail, e.ClientAddress));
        var logs = ReadLogs(server.Host.Get<OadmPaths>().LogsDirectory);

        string[] secrets = [DevicePassword, ListPassword, key, server.Host.AdminToken, AuthTokenStore.HashOf(key), key["oadm_".Length..]];
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, answers.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(audit, e => e.Contains(secret, StringComparison.Ordinal));
            Assert.DoesNotContain(secret, logs, StringComparison.Ordinal);
        }

        Assert.Contains("Device details", answers.ToString(), StringComparison.Ordinal); // the scan read real answers
    }

    private static string ReadLogs(string directory)
    {
        var text = new StringBuilder();
        foreach (var file in Directory.Exists(directory) ? Directory.GetFiles(directory) : [])
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            text.Append(reader.ReadToEnd());
        }

        return text.ToString();
    }
}
