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
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var server = await McpTestServer.StartAsync(accessClock: clock);
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
            ["oadm_devices_snapshot"] = new() { ["device_id"] = device.Id.ToString() },
            ["oadm_devices_refresh_preview"] = new() { ["filter"] = new JsonObject() },
            ["oadm_devices_refresh_run"] = new() { ["handle"] = "ended" },
            ["oadm_devices_remove_preview"] = new() { ["filter"] = new JsonObject { ["ids"] = new JsonArray(device.Id.ToString()) } },
            ["oadm_devices_remove_run"] = new() { ["handle"] = "ended" },
            ["oadm_devices_credentials_preview"] = new() { ["filter"] = new JsonObject(), ["credential"] = "root" },
            ["oadm_devices_credentials_run"] = new() { ["handle"] = "ended" },
            ["oadm_devices_add_preview"] = new() { ["addresses"] = new JsonArray("10.4.0.1"), ["keep_waiting"] = true },
            ["oadm_devices_add_run"] = new() { ["handle"] = "ended" },
            ["oadm_settings_get"] = [],
            ["oadm_files_list"] = [],
            ["oadm_files_delete_preview"] = new() { ["file_ids"] = new JsonArray("0123456789abcdef0123456789abcdef") },
            ["oadm_files_delete_run"] = new() { ["handle"] = "ended" },
            ["oadm_tasks_cancel_preview"] = new() { ["batch"] = Guid.NewGuid().ToString() },
            ["oadm_tasks_cancel_run"] = new() { ["handle"] = "ended" },
        };
        var areaKey = await server.CreateKeyAsync(name: "canary-areas"); // a key allows 20 calls a second: the areas' tools use their own
        var areaCalls = McpAreaToolTests.SecretScanCalls();
        Assert.Equal(calls.Keys.Concat(areaCalls.Keys).Order(StringComparer.Ordinal), await server.ToolNamesAsync(key));
        // 20 calls a second per key: ten calls with each key.
        var keys = new List<string> { key };
        var n = 0;
        foreach (var (tool, arguments) in calls)
        {
            if (n++ % 10 == 0 && n > 1)
            {
                keys.Add(await server.CreateKeyAsync(name: "canary" + keys.Count));
            }

            clock.Advance(TimeSpan.FromMinutes(2)); // the limits count per second and minute, per key and per user
            answers.Append((await server.CallToolAsync(keys[^1], tool, arguments)).ToJsonString());
        }

        foreach (var (tool, arguments) in areaCalls)
        {
            clock.Advance(TimeSpan.FromMinutes(2));
            answers.Append((await server.CallToolAsync(areaKey, tool, arguments)).ToJsonString());
        }

        answers.Append((await server.PostAsync(key, McpTestServer.Request("tools/list"))).Body?.ToJsonString());
        answers.Append((await server.PostAsync(key + "x", McpTestServer.Request("tools/list"))).Body?.ToJsonString());
        answers.Append((await server.PostAsync(server.Host.AdminToken, McpTestServer.Request("tools/list"))).Body?.ToJsonString());

        var audit = (await server.Host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries
            .Select(e => string.Join('|', e.UserName, e.Action, e.Target, e.Detail, e.ClientAddress));
        var logs = ReadLogs(server.Host.Get<OadmPaths>().LogsDirectory);

        string[] secrets = [DevicePassword, ListPassword, server.Host.AdminToken, areaKey, AuthTokenStore.HashOf(areaKey), areaKey["oadm_".Length..], .. keys, .. keys.Select(AuthTokenStore.HashOf), .. keys.Select(k => k["oadm_".Length..])];
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
