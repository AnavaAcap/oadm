using System.Text.Json.Nodes;

using Oadm.Core.Plugins;
using Oadm.Plugins.Restart;
using Oadm.Server.Tests.Support;
using Oadm.Server.Tools;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Mcp;

/// <summary>
/// OADM's own data over the tools (docs/spec/mcp.md "Tasks", "Other areas"): the task history summed up and filtered, for the
/// caller's devices only, and the server status without any secret.
/// </summary>
public sealed class McpOadmDataToolTests
{
    private static async Task<(McpTestServer Server, Proto.TaskInfo[] Tasks, string[] DeviceIds)> StartWithTasksAsync()
    {
        var server = await McpTestServer.StartAsync();
        var plugin = new RestartTaskPlugin(TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(2), TimeProvider.System);
        Assert.True(server.Host.Get<PluginRegistry>().RegisterTaskPlugin(plugin, new PluginOrigin("oadm.restart", "1.0.0", null)));
        var tasks = new List<Proto.TaskInfo>();
        var ids = new List<string>();
        for (var i = 1; i <= 3; i++)
        {
            var device = await DeviceServiceTests.AddDeviceAsync(server.Host, $"10.43.0.{i}", 4300 + i);
            ids.Add(device.Id.ToString());
            var run = await server.Host.Tasks.RunAsync(new Proto.RunTaskRequest { PluginId = RestartTaskPlugin.PluginId, DeviceIds = { device.Id.ToString() } });
            tasks.Add(await TestHelpers.WaitForTaskAsync(server.Host, run.TaskIds[0]));
        }

        return (server, [.. tasks], [.. ids]);
    }

    [Fact]
    public async Task TheSummaryGroupsTheHistoryAndTheListTakesTheSameFilters()
    {
        var (server, tasks, _) = await StartWithTasksAsync();
        await using var owned = server;
        var key = await server.CreateKeyAsync();

        var byPlugin = await server.ToolAsync(key, "oadm_tasks_summary", new JsonObject { ["group_by"] = "plugin" });
        Assert.Equal(3, byPlugin["total"]!.GetValue<int>());
        var group = Assert.Single(byPlugin["groups"]!.AsArray())!;
        Assert.Equal(RestartTaskPlugin.PluginId, group["key"]!.GetValue<string>());
        Assert.Equal(3, group["count"]!.GetValue<int>());
        Assert.Equal(3, group["devices"]!.GetValue<int>());
        Assert.Contains(group["example_task_id"]!.GetValue<string>(), tasks.Select(t => t.Id));
        Assert.Equal(0, byPlugin["other"]!.GetValue<int>());
        Assert.NotNull(byPlugin["oldest"]);

        // By result (the default): every state of the three tasks is counted once.
        var byResult = await server.ToolAsync(key, "oadm_tasks_summary");
        Assert.Equal(3, byResult["groups"]!.AsArray().Sum(g => g!["count"]!.GetValue<int>()));
        var states = byResult["counts"]!.AsObject();
        Assert.Equal(3, states.Sum(s => s.Value!.GetValue<int>()));

        // The filters of the summary are the list's: plugin, owner, text, since.
        Assert.Equal(3, (await server.ToolAsync(key, "oadm_tasks_list", new JsonObject { ["plugin"] = RestartTaskPlugin.PluginId }))["total"]!.GetValue<int>());
        Assert.Equal(0, (await server.ToolAsync(key, "oadm_tasks_list", new JsonObject { ["plugin"] = "oadm.firmware" }))["total"]!.GetValue<int>());
        Assert.Equal(3, (await server.ToolAsync(key, "oadm_tasks_list", new JsonObject { ["owner"] = "ADMIN@TESTPC" }))["total"]!.GetValue<int>());
        Assert.Equal(0, (await server.ToolAsync(key, "oadm_tasks_summary", new JsonObject { ["owner"] = "someone" }))["total"]!.GetValue<int>());
        Assert.Equal(3, (await server.ToolAsync(key, "oadm_tasks_list", new JsonObject { ["text"] = tasks[0].Name[..4].ToUpperInvariant() }))["total"]!.GetValue<int>());
        Assert.Equal(0, (await server.ToolAsync(key, "oadm_tasks_list", new JsonObject { ["since"] = "2999-01-01T00:00:00Z" }))["total"]!.GetValue<int>());
        Assert.Equal(3, (await server.ToolAsync(key, "oadm_tasks_summary", new JsonObject { ["since"] = "2000-01-01T00:00:00Z", ["group_by"] = "day" }))["total"]!.GetValue<int>());

        var bad = await server.CallToolAsync(key, "oadm_tasks_summary", new JsonObject { ["group_by"] = "color" });
        Assert.True(bad["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AKeyOfADeviceGroupSumsUpOnlyTheTasksOfItsDevices()
    {
        var (server, _, ids) = await StartWithTasksAsync();
        await using var owned = server;
        var tag = await server.Host.Tags.CreateAsync(new Proto.CreateTagRequest { Name = "Lobby" });
        await server.Host.Tags.SetDeviceTagsAsync(new Proto.SetDeviceTagsRequest { DeviceIds = { ids[0] }, Add = { "Lobby" } });
        var group = await GroupCallers.GroupAsync(server.Host, "Lobby group", new Proto.DeviceGroupRules { TagIds = { tag.Id } });
        var key = await server.CreateKeyAsync(name: "Lobby-bot", roleId: Oadm.Core.Auth.BuiltInRoles.OperatorId.ToString(), groupId: group.ToString());

        var summary = await server.ToolAsync(key, "oadm_tasks_summary", new JsonObject { ["group_by"] = "device" });
        Assert.Equal(1, summary["total"]!.GetValue<int>());
        Assert.Equal(ids[0], Assert.Single(summary["groups"]!.AsArray())!["key"]!.GetValue<string>());
        var list = await server.ToolAsync(key, "oadm_tasks_list");
        Assert.Equal(1, list["total"]!.GetValue<int>());
    }

    [Fact]
    public async Task TheServerStatusShowsLicensePluginsMcpTheAssistantAndTheCveListWithoutASecret()
    {
        await using var server = await McpTestServer.StartAsync();
        var key = await server.CreateKeyAsync();
        var status = await server.ToolAsync(key, "oadm_server_status", new JsonObject { ["plugins"] = "all" });
        Assert.Equal("None", status["license"]!["status"]!.GetValue<string>());
        Assert.True(status["mcp"]!["on"]!.GetValue<bool>());
        Assert.False(status["assistant"]!["on"]!.GetValue<bool>());
        Assert.Equal(string.Empty, status["assistant"]!["provider"]!.GetValue<string>());
        Assert.True(status["vulnerability_list"]!["count"]!.GetValue<int>() >= 0);
        Assert.Equal(
            status["plugins_on"]!.GetValue<int>() + status["plugins_off"]!.GetValue<int>(),
            status["plugins"]!.AsArray().Count);

        // Never the server's ID (which licenses name), a customer or a key.
        var license = await new Proto.LicenseService.LicenseServiceClient(server.Host.Invoker).GetLicenseAsync(new Proto.Empty());
        var text = status.ToJsonString();
        Assert.DoesNotContain(license.ServerId, text, StringComparison.Ordinal);
        Assert.DoesNotContain("server_id", text, StringComparison.Ordinal);
        Assert.DoesNotContain("customer", text, StringComparison.Ordinal);
        Assert.DoesNotContain("api_key", text, StringComparison.Ordinal);
        Assert.DoesNotContain("certificate", text, StringComparison.Ordinal);
    }
}
