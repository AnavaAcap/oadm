using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Oadm.Core.Auth;
using Oadm.Core.Events;
using Oadm.Core.Plugins;
using Oadm.Plugins.Restart;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;
using Oadm.Server.Assistant;
using Oadm.Server.Tests.Support;
using Oadm.Server.Tools;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Mcp;

/// <summary>
/// The host actions of stage 1 of "everything a user can do" (docs/spec/mcp.md "Host actions", decision 2026-11-15): tags,
/// the waiting list, the task history, the dialogs' reads, the rule page's actions, recipients, compliance, sites, the live
/// view's controls, the vulnerability list and the version check. Each change: the preview writes nothing, the run does it
/// once with the handle of its own preview, the audit as for the client, the rights of the role and the device group, and
/// the Assistant's card of the preview.
/// </summary>
public sealed class McpHostActionTests
{
    /// <summary>The tools of this stage (<see cref="McpToolTests"/> lists them with the others).</summary>
    public static readonly string[] ToolNames =
    [
        "oadm_compliance_check_delete_preview", "oadm_compliance_check_delete_run", "oadm_compliance_check_enable_preview",
        "oadm_compliance_check_enable_run", "oadm_compliance_check_now_preview", "oadm_compliance_check_now_run",
        "oadm_compliance_check_save_preview", "oadm_compliance_check_save_run", "oadm_compliance_devices", "oadm_compliance_enable_preview",
        "oadm_compliance_enable_run", "oadm_compliance_kinds", "oadm_devices_optics", "oadm_devices_optics_preview", "oadm_devices_optics_run",
        "oadm_devices_ptz", "oadm_devices_ptz_preview", "oadm_devices_ptz_run", "oadm_devices_rtsp_port_preview", "oadm_devices_rtsp_port_run",
        "oadm_events_clear_log_preview", "oadm_events_clear_log_run", "oadm_recipients_certificates", "oadm_recipients_delete_preview",
        "oadm_recipients_delete_run", "oadm_recipients_save_preview", "oadm_recipients_save_run", "oadm_recipients_test_preview",
        "oadm_recipients_test_run", "oadm_recipients_types", "oadm_rules_duplicate_preview", "oadm_rules_duplicate_run",
        "oadm_rules_enable_preview", "oadm_rules_enable_run", "oadm_rules_test_action_preview", "oadm_rules_test_action_run",
        "oadm_sites_rename_preview", "oadm_sites_rename_run", "oadm_tags_delete_preview", "oadm_tags_delete_run", "oadm_tags_update_preview",
        "oadm_tags_update_run", "oadm_task_query", "oadm_tasks_delete_all_preview", "oadm_tasks_delete_all_run", "oadm_tasks_delete_preview",
        "oadm_tasks_delete_run", "oadm_update_check", "oadm_vulnerabilities_update_preview", "oadm_vulnerabilities_update_run",
        "oadm_waiting_list", "oadm_waiting_remove_preview", "oadm_waiting_remove_run",
        .. McpLoginToolTests.ToolNames,
    ];

    /// <summary>The gRPC methods the tools of this stage call besides those of the other stages.</summary>
    public static readonly string[] Methods =
    [
        "/oadm.v1.ComplianceService/CheckNow", "/oadm.v1.ComplianceService/DeleteCheck", "/oadm.v1.ComplianceService/GetCheck",
        "/oadm.v1.ComplianceService/ListResults", "/oadm.v1.ComplianceService/PlanCheck", "/oadm.v1.ComplianceService/SaveCheck",
        "/oadm.v1.ComplianceService/SetCheckEnabled", "/oadm.v1.ComplianceService/SetEnabled",
        "/oadm.v1.DiscoveryService/StartZeroConf",
        "/oadm.v1.EventService/CheckRecipient", "/oadm.v1.EventService/ClearLog", "/oadm.v1.EventService/DeleteRecipient",
        "/oadm.v1.EventService/DuplicateRule", "/oadm.v1.EventService/ListRecipientCertificates", "/oadm.v1.EventService/SaveRecipient",
        "/oadm.v1.EventService/SetRuleEnabled", "/oadm.v1.EventService/TestAction", "/oadm.v1.EventService/TestRecipient",
        "/oadm.v1.LiveViewService/GetOptics", "/oadm.v1.LiveViewService/GetPtz", "/oadm.v1.LiveViewService/GetRtspPort",
        "/oadm.v1.LiveViewService/MovePtz", "/oadm.v1.LiveViewService/SetOptics", "/oadm.v1.LiveViewService/SetRtspPort",
        "/oadm.v1.SiteService/RenameSite",
        "/oadm.v1.TagService/Delete", "/oadm.v1.TagService/Update",
        "/oadm.v1.TaskService/Delete", "/oadm.v1.TaskService/DeleteAll", "/oadm.v1.TaskService/Query",
        "/oadm.v1.UpdateService/Get", "/oadm.v1.VulnerabilityService/Refresh", "/oadm.v1.WaitingDeviceService/Remove",
        .. McpLoginToolTests.Methods,
    ];

    /// <summary>The run tools of this stage: each takes only the handle of its preview.</summary>
    public static string[] RunTools => [.. ToolNames.Where(n => n.EndsWith("_run", StringComparison.Ordinal))];

    /// <summary>The runs that only add or send (<c>destructiveHint</c> false).</summary>
    public static readonly string[] AddingRuns =
    [
        "oadm_compliance_check_now_run", "oadm_recipients_test_run", "oadm_rules_duplicate_run", "oadm_rules_test_action_run", "oadm_vulnerabilities_update_run",
        "oadm_credentials_add_run",
    ];

    /// <summary>The reads and previews that ask a device or act outside OADM (no plain read).</summary>
    public static readonly string[] ActsOutside =
    [
        "oadm_compliance_check_now_run", "oadm_devices_optics", "oadm_devices_optics_preview", "oadm_devices_ptz", "oadm_devices_ptz_preview",
        "oadm_task_query", "oadm_vulnerabilities_update_run", "oadm_devices_login_run", "oadm_devices_first_password_preview",
    ];

    /// <summary>One call of every tool of this stage for the scan of <see cref="McpSecretTests"/>.</summary>
    public static Dictionary<string, JsonObject> SecretScanCalls(string deviceId)
    {
        var calls = new Dictionary<string, JsonObject>(StringComparer.Ordinal)
        {
            ["oadm_tags_update_preview"] = new() { ["tag"] = "none", ["color"] = "red" },
            ["oadm_tags_delete_preview"] = new() { ["tag"] = "none" },
            ["oadm_waiting_list"] = [],
            ["oadm_waiting_remove_preview"] = new() { ["all"] = true },
            ["oadm_tasks_delete_preview"] = new() { ["batch"] = Guid.NewGuid().ToString() },
            ["oadm_tasks_delete_all_preview"] = [],
            ["oadm_task_query"] = new() { ["task"] = "oadm.users", ["device_id"] = deviceId, ["method"] = "listUsers" },
            ["oadm_rules_enable_preview"] = new() { ["rule_id"] = Guid.NewGuid().ToString(), ["enabled"] = true },
            ["oadm_rules_duplicate_preview"] = new() { ["rule_id"] = Guid.NewGuid().ToString() },
            ["oadm_rules_test_action_preview"] = new() { ["rule_id"] = Guid.NewGuid().ToString() },
            ["oadm_events_clear_log_preview"] = [],
            ["oadm_recipients_types"] = [],
            ["oadm_recipients_certificates"] = [],
            ["oadm_recipients_save_preview"] = new() { ["name"] = "Scan", ["type_id"] = "none" },
            ["oadm_recipients_delete_preview"] = new() { ["recipient_id"] = Guid.NewGuid().ToString() },
            ["oadm_recipients_test_preview"] = new() { ["recipient_id"] = Guid.NewGuid().ToString() },
            ["oadm_compliance_devices"] = new() { ["check_id"] = Guid.NewGuid().ToString() },
            ["oadm_compliance_kinds"] = [],
            ["oadm_compliance_enable_preview"] = new() { ["enabled"] = false },
            ["oadm_compliance_check_save_preview"] = new() { ["name"] = "Scan", ["type_id"] = "none" },
            ["oadm_compliance_check_delete_preview"] = new() { ["check_id"] = Guid.NewGuid().ToString() },
            ["oadm_compliance_check_enable_preview"] = new() { ["check_id"] = Guid.NewGuid().ToString(), ["enabled"] = true },
            ["oadm_compliance_check_now_preview"] = [],
            ["oadm_sites_rename_preview"] = new() { ["site_id"] = Guid.NewGuid().ToString(), ["name"] = "Scan" },
            ["oadm_devices_optics"] = new() { ["device_id"] = deviceId },
            ["oadm_devices_optics_preview"] = new() { ["device_id"] = deviceId, ["autofocus"] = true },
            ["oadm_devices_ptz"] = new() { ["device_id"] = deviceId },
            ["oadm_devices_ptz_preview"] = new() { ["device_id"] = deviceId, ["move"] = "home" },
            ["oadm_devices_rtsp_port_preview"] = new() { ["filter"] = new JsonObject { ["ids"] = new JsonArray(deviceId) }, ["port"] = 8554 },
            ["oadm_vulnerabilities_update_preview"] = [],
            ["oadm_update_check"] = [],
            ["oadm_devices_login_preview"] = new() { ["filter"] = new JsonObject { ["ids"] = new JsonArray(deviceId) }, ["user_name"] = "root" },
            ["oadm_devices_first_password_preview"] = new() { ["filter"] = new JsonObject { ["ids"] = new JsonArray(deviceId) } },
            ["oadm_credentials_add_preview"] = new() { ["user_name"] = "scan" },
            ["oadm_credentials_remove_preview"] = new() { ["user_name"] = "nobody" },
        };
        foreach (var run in RunTools)
        {
            calls[run] = new JsonObject { ["handle"] = "ended" };
        }

        return calls;
    }

    private static string Text(JsonObject result) => result["content"]![0]!["text"]!.GetValue<string>();

    private static async Task<IReadOnlyList<Proto.AuditEntry>> AuditAsync(McpTestServer server) =>
        (await server.Host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;

    /// <summary>The Assistant's card of a preview's answer, built as the panel gets it.</summary>
    private static Proto.AssistantPreviewCard Card(McpTestServer server, string preview, JsonObject answer, JsonObject? arguments = null)
    {
        var registry = server.Host.Get<ToolRegistry>();
        Assert.True(registry.TryGet(preview, out var tool));
        registry.TryGet(AssistantTools.RunOf(preview), out var run);
        var card = AssistantCards.Build(tool, run, answer["handle"]?.GetValue<string>() ?? "h", answer, JsonSerializer.SerializeToElement(arguments ?? []));
        Assert.NotEmpty(card.Details);
        Assert.False(string.IsNullOrEmpty(card.CountText));
        return card;
    }

    /// <summary>A run refuses its handle a second time, and another preview's handle answers like an ended one.</summary>
    private static async Task AssertOnceAsync(McpTestServer server, string key, string run, string handle)
    {
        var again = await server.CallToolAsync(key, run, new JsonObject { ["handle"] = handle });
        Assert.True(again["isError"]!.GetValue<bool>(), run);
        Assert.Contains(AreaListing.PreviewEnded, Text(again), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATagIsRenamedRecoloredAndDeletedOnlyByTheRunOnceAndAudited()
    {
        await using var server = await McpTestServer.StartAsync();
        var a = await DeviceServiceTests.AddDeviceAsync(server.Host, "10.61.0.1", 9101);
        var b = await DeviceServiceTests.AddDeviceAsync(server.Host, "10.61.0.2", 9102);
        await server.Host.Tags.SetDeviceTagsAsync(new Proto.SetDeviceTagsRequest { DeviceIds = { a.Id.ToString(), b.Id.ToString() }, Add = { "Lobby" } });
        await server.Host.Tags.CreateAsync(new Proto.CreateTagRequest { Name = "Garage" });
        var key = await server.CreateKeyAsync();

        var taken = await server.CallToolAsync(key, "oadm_tags_update_preview", new JsonObject { ["tag"] = "Lobby", ["new_name"] = "garage" });
        Assert.True(taken["isError"]!.GetValue<bool>());
        Assert.Contains(TagTools.NameTaken, Text(taken), StringComparison.Ordinal);

        var preview = await server.ToolAsync(key, "oadm_tags_update_preview", new JsonObject { ["tag"] = "lobby", ["new_name"] = "Entrance", ["color"] = "red" });
        Assert.Equal(("Lobby", "Entrance", "red", 2), (preview["tag"]!.GetValue<string>(), preview["new_name"]!.GetValue<string>(), preview["color"]!.GetValue<string>(), preview["devices"]!.GetValue<int>()));
        Assert.Contains((await server.Host.Tags.ListAsync(new Proto.Empty())).Tags, t => t.Name == "Lobby"); // nothing written
        var card = Card(server, "oadm_tags_update_preview", preview);
        Assert.Equal("2 devices", card.CountText);
        Assert.False(card.Destructive);

        var handle = preview["handle"]!.GetValue<string>();
        var done = await server.ToolAsync(key, "oadm_tags_update_run", new JsonObject { ["handle"] = handle });
        Assert.Equal(("Entrance", "red"), (done["tag"]!.GetValue<string>(), done["color"]!.GetValue<string>()));
        await AssertOnceAsync(server, key, "oadm_tags_update_run", handle);
        Assert.Contains(await AuditAsync(server), e => e.Action == AuditActions.TagRenamed && e.Target == "Lobby" && e.UserName.StartsWith("MCP ", StringComparison.Ordinal));
        Assert.Contains(await AuditAsync(server), e => e.Action == AuditActions.TagRecolored);

        var delete = await server.ToolAsync(key, "oadm_tags_delete_preview", new JsonObject { ["tag"] = "Entrance" });
        Assert.Equal(2, delete["devices"]!.GetValue<int>());
        Assert.True(Card(server, "oadm_tags_delete_preview", delete).Destructive);
        Assert.Contains((await server.Host.Tags.ListAsync(new Proto.Empty())).Tags, t => t.Name == "Entrance");

        // A handle is taken only by its own run tool: the rename run refuses the delete's handle and leaves it for the delete.
        var wrong = await server.CallToolAsync(key, "oadm_tags_update_run", new JsonObject { ["handle"] = delete["handle"]!.GetValue<string>() });
        Assert.True(wrong["isError"]!.GetValue<bool>());
        var deleted = await server.ToolAsync(key, "oadm_tags_delete_run", new JsonObject { ["handle"] = delete["handle"]!.GetValue<string>() });
        Assert.Equal(2, deleted["devices_changed"]!.GetValue<int>());
        Assert.DoesNotContain((await server.Host.Tags.ListAsync(new Proto.Empty())).Tags, t => t.Name == "Entrance");
        Assert.Contains(await AuditAsync(server), e => e.Action == AuditActions.TagDeleted && e.Target == "Entrance");
    }

    [Fact]
    public async Task ARoleWithoutTheRightsAndAGroupUserListNoneOfTheChangesAndAGroupSeesOnlyItsDevices()
    {
        await using var server = await McpTestServer.StartAsync();
        var mine = await DeviceServiceTests.AddDeviceAsync(server.Host, "10.62.0.1", 9201);
        var other = await DeviceServiceTests.AddDeviceAsync(server.Host, "10.62.0.2", 9202);
        await server.Host.Tags.SetDeviceTagsAsync(new Proto.SetDeviceTagsRequest { DeviceIds = { mine.Id.ToString() }, Add = { "Lobby" } });
        var roles = new Proto.RoleService.RoleServiceClient(server.Host.Invoker);
        var saved = await roles.SaveRolesAsync(new Proto.SaveRolesRequest { Roles = { new Proto.RoleChange { Name = "Lookers", Permissions = { } } } });
        var viewer = await server.CreateKeyAsync(await server.Host.InvokerForRoleAsync("looker", Guid.Parse(saved.Roles.Single(r => r.Name == "Lookers").Id)));

        var names = await server.ToolNamesAsync(viewer);
        Assert.All(RunTools, run => Assert.DoesNotContain(run, names));
        Assert.All(ToolNames.Where(n => n.EndsWith("_preview", StringComparison.Ordinal)), preview => Assert.DoesNotContain(preview, names));
        var refused = await server.CallToolAsync(viewer, "oadm_tags_delete_preview", new JsonObject { ["tag"] = "Lobby" });
        Assert.True(refused["isError"]!.GetValue<bool>());
        Assert.Contains("Your role does not allow", Text(refused), StringComparison.Ordinal);

        // A user of a device group: no tool that needs all devices (tags, rules, recipients, all tasks), and only its devices.
        var group = await GroupCallers.ForTagAsync(server.Host, "tech", "Lobby");
        var groupKey = await server.CreateKeyAsync(group.Invoker);
        var groupNames = await server.ToolNamesAsync(groupKey);
        Assert.DoesNotContain("oadm_tags_delete_preview", groupNames);
        Assert.DoesNotContain("oadm_tasks_delete_all_preview", groupNames);
        Assert.DoesNotContain("oadm_recipients_save_preview", groupNames);
        Assert.DoesNotContain("oadm_rules_enable_preview", groupNames);
        Assert.Contains("oadm_devices_rtsp_port_preview", groupNames);
        var outside = await server.CallToolAsync(groupKey, "oadm_devices_rtsp_port_preview", new JsonObject { ["filter"] = new JsonObject { ["ids"] = new JsonArray(other.Id.ToString()) }, ["port"] = 8554 });
        Assert.True(outside["isError"]!.GetValue<bool>());
        var inside = await server.ToolAsync(groupKey, "oadm_devices_rtsp_port_preview", new JsonObject { ["filter"] = new JsonObject(), ["port"] = 8554 });
        Assert.Equal(1, inside["devices"]!.GetValue<int>()); // the group's one device of two
    }

    [Fact]
    public async Task TheWaitingListPagesAndItsEntriesLeaveOnlyWithTheRun()
    {
        await using var server = await McpTestServer.StartAsync();
        var waiting = new Proto.WaitingDeviceService.WaitingDeviceServiceClient(server.Host.Invoker);
        var add = new Proto.AddWaitingRequest { Source = "Import from file site.csv" };
        add.Lines.AddRange(Enumerable.Range(1, 5).Select(i => new Proto.WaitingLine { Address = $"10.63.0.{i}", UserName = "root", Password = "canary-waiting-1", Tags = { "New" } }));
        await waiting.AddAsync(add);
        var key = await server.CreateKeyAsync();

        var first = await server.ToolAsync(key, "oadm_waiting_list", new JsonObject { ["limit"] = 2 });
        Assert.Equal(5, first["total"]!.GetValue<int>());
        Assert.Equal(["10.63.0.1", "10.63.0.2"], first["entries"]!.AsArray().Select(e => e!["address"]!.GetValue<string>()));
        Assert.True(first["entries"]![0]!["has_login"]!.GetValue<bool>());
        Assert.DoesNotContain("canary-waiting-1", first.ToJsonString(), StringComparison.Ordinal);
        var second = await server.ToolAsync(key, "oadm_waiting_list", new JsonObject { ["limit"] = 2, ["cursor"] = first["next_cursor"]!.GetValue<string>() });
        Assert.Equal(["10.63.0.3", "10.63.0.4"], second["entries"]!.AsArray().Select(e => e!["address"]!.GetValue<string>()));

        var ids = first["entries"]!.AsArray().Select(e => (JsonNode)e!["id"]!.GetValue<string>()).ToArray();
        var preview = await server.ToolAsync(key, "oadm_waiting_remove_preview", new JsonObject { ["ids"] = new JsonArray(ids) });
        Assert.Equal(2, preview["will_remove"]!.GetValue<int>());
        Assert.Equal(5, (await waiting.ListAsync(new Proto.ListWaitingRequest())).TotalCount); // nothing written
        var card = Card(server, "oadm_waiting_remove_preview", preview);
        Assert.Equal("2 addresses", card.CountText);
        Assert.True(card.Destructive);
        var run = await server.ToolAsync(key, "oadm_waiting_remove_run", new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() });
        Assert.Equal(3, run["left"]!.GetValue<int>());
        await AssertOnceAsync(server, key, "oadm_waiting_remove_run", preview["handle"]!.GetValue<string>());

        var both = await server.CallToolAsync(key, "oadm_waiting_remove_preview", new JsonObject { ["ids"] = new JsonArray(ids[0]!.DeepClone()), ["all"] = true });
        Assert.True(both["isError"]!.GetValue<bool>());
        var all = await server.ToolAsync(key, "oadm_waiting_remove_preview", new JsonObject { ["all"] = true });
        Assert.Equal(3, all["will_remove"]!.GetValue<int>());
    }

    [Fact]
    public async Task TasksLeaveTheHistoryOnlyWithTheRunAndDeleteAllIsAPreviewAndARun()
    {
        await using var server = await McpTestServer.StartAsync();
        var plugins = server.Host.Get<PluginRegistry>();
        Assert.True(plugins.RegisterTaskPlugin(new RestartTaskPlugin(TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(2), TimeProvider.System), new PluginOrigin("oadm.restart", "1.0.0", null)));
        var device = await DeviceServiceTests.AddDeviceAsync(server.Host, "10.64.0.1", 9401);
        var batch = await server.Host.Tasks.RunAsync(new Proto.RunTaskRequest { PluginId = RestartTaskPlugin.PluginId, DeviceIds = { device.Id.ToString() } });
        var second = await server.Host.Tasks.RunAsync(new Proto.RunTaskRequest { PluginId = RestartTaskPlugin.PluginId, DeviceIds = { device.Id.ToString() } });
        await Eventually.TrueAsync(async () => (await server.Host.Tasks.ListAsync(new Proto.ListTasksRequest())).Tasks.All(t => t.State is Proto.TaskState.Done or Proto.TaskState.Failed), "both tasks ended");
        var key = await server.CreateKeyAsync();

        var preview = await server.ToolAsync(key, "oadm_tasks_delete_preview", new JsonObject { ["batch"] = batch.BatchId });
        Assert.Equal(1, preview["will_delete"]!.GetValue<int>());
        Assert.Equal(2, (await server.Host.Tasks.ListAsync(new Proto.ListTasksRequest())).Tasks.Count); // nothing written
        Assert.True(Card(server, "oadm_tasks_delete_preview", preview).Destructive);
        var run = await server.ToolAsync(key, "oadm_tasks_delete_run", new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() });
        Assert.Equal(1, run["deleted"]!.GetValue<int>());
        Assert.Equal([second.BatchId], (await server.Host.Tasks.ListAsync(new Proto.ListTasksRequest())).Tasks.Select(t => t.BatchId));
        await AssertOnceAsync(server, key, "oadm_tasks_delete_run", preview["handle"]!.GetValue<string>());

        var all = await server.ToolAsync(key, "oadm_tasks_delete_all_preview");
        Assert.Equal(1, all["will_delete"]!.GetValue<int>());
        var card = Card(server, "oadm_tasks_delete_all_preview", all);
        Assert.Equal("1 task", card.CountText);
        Assert.True(card.Destructive);
        Assert.Single((await server.Host.Tasks.ListAsync(new Proto.ListTasksRequest())).Tasks);
        var cleared = await server.ToolAsync(key, "oadm_tasks_delete_all_run", new JsonObject { ["handle"] = all["handle"]!.GetValue<string>() });
        Assert.Equal(1, cleared["deleted"]!.GetValue<int>());
        Assert.Empty((await server.Host.Tasks.ListAsync(new Proto.ListTasksRequest())).Tasks);
        Assert.Contains(await AuditAsync(server), e => e.Action == AuditActions.TasksDeletedAll && e.UserName.StartsWith("MCP ", StringComparison.Ordinal));

        var empty = await server.ToolAsync(key, "oadm_tasks_delete_all_preview");
        Assert.Null(empty["handle"]);
        Assert.Contains(TaskChangeTools.HistoryEmpty, empty["warnings"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATaskReadIsRefusedForTheConfigurationReadsAndScrubsWhatMayBeASecret()
    {
        await using var server = await McpTestServer.StartAsync();
        var device = await DeviceServiceTests.AddDeviceAsync(server.Host, "10.65.0.1", 9501);
        var key = await server.CreateKeyAsync();
        var config = await server.CallToolAsync(key, "oadm_task_query", new JsonObject { ["task"] = "oadm.device-config.export", ["device_id"] = device.Id.ToString(), ["method"] = "export" });
        Assert.True(config["isError"]!.GetValue<bool>());
        Assert.Contains(TaskChangeTools.QueryNotOffered, Text(config), StringComparison.Ordinal);
        var unknown = await server.CallToolAsync(key, "oadm_task_query", new JsonObject { ["task"] = "oadm.none", ["device_id"] = device.Id.ToString(), ["method"] = "x" });
        Assert.True(unknown["isError"]!.GetValue<bool>());

        var answer = JsonNode.Parse("""{"users":[{"name":"root","password":"p1","role":"admin"}],"parameters":[{"name":"Network.SNMP.Community","value":"public2"},{"name":"Image.Resolution","value":"1080p"}],"apiKey":"k"}""");
        Assert.Equal(3, TaskChangeTools.Scrub(answer));
        var text = answer!.ToJsonString();
        Assert.DoesNotContain("p1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("public2", text, StringComparison.Ordinal);
        Assert.Contains("1080p", text, StringComparison.Ordinal);
        Assert.Contains("root", text, StringComparison.Ordinal);
    }

    private static async Task<McpTestServer> StartWithActionsAsync()
    {
        var server = await McpTestServer.StartAsync();
        var plugins = server.Host.Get<PluginRegistry>();
        plugins.RegisterActionPlugin(new NotifyAction(), new PluginOrigin("test.host.notify", "1.0", null) { DisplayName = "Notify" });
        plugins.RegisterActionPlugin(new RelayAction(), new PluginOrigin("test.host.relay", "1.0", null) { DisplayName = "Relay" });
        return server;
    }

    private static Proto.EventService.EventServiceClient Events(McpTestServer server) => new(server.Host.Invoker);

    private static RuleDefinition Rule(string name) => new()
    {
        Name = name,
        Conditions = [new RuleCondition { Kind = RuleConditionKind.Event, EventTypeId = BuiltInEvents.Unreachable, Devices = new DeviceSelector { Mode = DeviceSelectorMode.All } }],
        Actions = [new RuleAction { ActionId = "test.host.notify", Settings = new JsonObject { ["message"] = "{device} down", ["token"] = "canary-rule-token" } }],
    };

    [Fact]
    public async Task ARuleIsTurnedOnDuplicatedAndTestedOnlyByItsRunsAndTheEventLogIsCleared()
    {
        await using var server = await StartWithActionsAsync();
        var saved = await Events(server).SaveRuleAsync(new Proto.SaveRuleRequest { DefinitionJson = Rule("Camera down").ToJson() });
        var id = saved.Summary.Id;
        var key = await server.CreateKeyAsync();

        var on = await server.ToolAsync(key, "oadm_rules_enable_preview", new JsonObject { ["rule_id"] = id, ["enabled"] = true });
        Assert.False((await Events(server).GetRuleAsync(new Proto.RuleId { Id = id })).Summary.Enabled); // nothing written
        Assert.Equal("1 rule", Card(server, "oadm_rules_enable_preview", on).CountText);
        var turned = await server.ToolAsync(key, "oadm_rules_enable_run", new JsonObject { ["handle"] = on["handle"]!.GetValue<string>() });
        Assert.True(turned["enabled"]!.GetValue<bool>());
        Assert.Contains(await AuditAsync(server), e => e.Action == AuditActions.RuleTurnedOn && e.Target == "Camera down");
        var again = await server.ToolAsync(key, "oadm_rules_enable_preview", new JsonObject { ["rule_id"] = id, ["enabled"] = true });
        Assert.Null(again["handle"]);

        var copy = await server.ToolAsync(key, "oadm_rules_duplicate_preview", new JsonObject { ["rule_id"] = id });
        Assert.Equal("Camera down (copy)", copy["copy_name"]!.GetValue<string>());
        Assert.Single((await Events(server).ListRulesAsync(new Proto.Empty())).Rules);
        Card(server, "oadm_rules_duplicate_preview", copy);
        var made = await server.ToolAsync(key, "oadm_rules_duplicate_run", new JsonObject { ["handle"] = copy["handle"]!.GetValue<string>() });
        var copied = await Events(server).GetRuleAsync(new Proto.RuleId { Id = made["rule_id"]!.GetValue<string>() });
        Assert.False(copied.Summary.Enabled);
        Assert.Contains("token", RuleDefinition.FromJson(copied.DefinitionJson).Actions[0].StoredSecretFields); // the server kept the password

        var test = await server.ToolAsync(key, "oadm_rules_test_action_preview", new JsonObject { ["rule_id"] = id });
        Assert.Equal(0, NotifyAction.Runs);
        Assert.Equal("1 action", Card(server, "oadm_rules_test_action_preview", test).CountText);
        var sent = await server.ToolAsync(key, "oadm_rules_test_action_run", new JsonObject { ["handle"] = test["handle"]!.GetValue<string>() });
        Assert.True(sent["ok"]!.GetValue<bool>(), sent.ToJsonString());
        Assert.Equal(1, NotifyAction.Runs);
        Assert.Equal("canary-rule-token", NotifyAction.LastToken); // the stored password, never passed through the tool
        Assert.DoesNotContain("canary-rule-token", test.ToJsonString() + sent.ToJsonString(), StringComparison.Ordinal);
        await AssertOnceAsync(server, key, "oadm_rules_test_action_run", test["handle"]!.GetValue<string>());

        var log = server.Host.Get<EventLog>();
        log.Write(EventLogKind.Action, EventLogResult.Ok, "Step 1");
        log.Write(EventLogKind.Action, EventLogResult.Ok, "Step 2");
        await log.FlushAsync(CancellationToken.None);
        var clear = await server.ToolAsync(key, "oadm_events_clear_log_preview");
        Assert.True(clear["will_delete"]!.GetValue<int>() >= 2);
        Assert.True(Card(server, "oadm_events_clear_log_preview", clear).Destructive);
        var cleared = await server.ToolAsync(key, "oadm_events_clear_log_run", new JsonObject { ["handle"] = clear["handle"]!.GetValue<string>() });
        Assert.True(cleared["deleted"]!.GetValue<int>() >= 2);
        Assert.Contains(await AuditAsync(server), e => e.Action == AuditActions.EventLogCleared);
    }

    [Fact]
    public async Task ARecipientIsCheckedWithoutSavingSavedTestedAndDeletedAndNeverTakesAPassword()
    {
        await using var server = await StartWithActionsAsync();
        var key = await server.CreateKeyAsync();
        var types = await server.ToolAsync(key, "oadm_recipients_types");
        Assert.Contains(types["types"]!.AsArray(), t => t!["type_id"]!.GetValue<string>() == "test.host.relay-type");

        var password = await server.CallToolAsync(key, "oadm_recipients_save_preview", new JsonObject
        {
            ["name"] = "Ops", ["type_id"] = "test.host.relay-type", ["settings"] = new JsonObject { ["url"] = "https://relay.example.com/a", ["token"] = "canary-new" },
        });
        Assert.True(password["isError"]!.GetValue<bool>());
        Assert.Contains(RecipientChangeTools.UseSetSecrets, Text(password), StringComparison.Ordinal);
        Assert.DoesNotContain("canary-new", Text(password), StringComparison.Ordinal);
        var login = await server.CallToolAsync(key, "oadm_recipients_save_preview", new JsonObject
        {
            ["name"] = "Ops", ["type_id"] = "test.host.relay-type", ["settings"] = new JsonObject { ["url"] = "https://user:pw@relay.example.com/a" },
        });
        Assert.True(login["isError"]!.GetValue<bool>());

        var preview = await server.ToolAsync(key, "oadm_recipients_save_preview", new JsonObject
        {
            ["name"] = "Ops", ["type_id"] = "test.host.relay-type", ["settings"] = new JsonObject { ["url"] = "https://relay.example.com/hook?key=abc" },
        });
        Assert.Equal("create", preview["change"]!.GetValue<string>());
        Assert.DoesNotContain("abc", preview["target"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Empty((await Events(server).ListRecipientsAsync(new Proto.Empty())).Recipients); // nothing saved
        Assert.DoesNotContain(await AuditAsync(server), e => e.Action == AuditActions.RecipientCreated);
        Assert.Equal("1 recipient", Card(server, "oadm_recipients_save_preview", preview).CountText);
        var saved = await server.ToolAsync(key, "oadm_recipients_save_run", new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() });
        var id = saved["recipient_id"]!.GetValue<string>();
        Assert.Contains(await AuditAsync(server), e => e.Action == AuditActions.RecipientCreated && e.Target == "Ops");

        var test = await server.ToolAsync(key, "oadm_recipients_test_preview", new JsonObject { ["recipient_id"] = id });
        Assert.DoesNotContain(await AuditAsync(server), e => e.Action == AuditActions.RecipientTested);
        Card(server, "oadm_recipients_test_preview", test);
        var tested = await server.ToolAsync(key, "oadm_recipients_test_run", new JsonObject { ["handle"] = test["handle"]!.GetValue<string>() });
        Assert.NotNull(tested["ok"]);
        Assert.Contains(await AuditAsync(server), e => e.Action == AuditActions.RecipientTested);

        // Used by a rule: the preview names the rule and gives no handle; free again, it deletes.
        var rule = new RuleDefinition
        {
            Name = "Relay rule",
            Conditions = [new RuleCondition { Kind = RuleConditionKind.Event, EventTypeId = BuiltInEvents.Unreachable, Devices = new DeviceSelector { Mode = DeviceSelectorMode.All } }],
            Actions = [new RuleAction { ActionId = "test.host.relay", Settings = new JsonObject { ["recipient"] = id } }],
        };
        var ruleId = (await Events(server).SaveRuleAsync(new Proto.SaveRuleRequest { DefinitionJson = rule.ToJson() })).Summary.Id;
        var used = await server.ToolAsync(key, "oadm_recipients_delete_preview", new JsonObject { ["recipient_id"] = id });
        Assert.Null(used["handle"]);
        Assert.Equal(["Relay rule"], used["used_by"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Contains(RecipientChangeTools.InUse, used["warnings"]!.ToJsonString(), StringComparison.Ordinal);
        await Events(server).DeleteRuleAsync(new Proto.RuleId { Id = ruleId });
        var delete = await server.ToolAsync(key, "oadm_recipients_delete_preview", new JsonObject { ["recipient_id"] = id });
        Assert.True(Card(server, "oadm_recipients_delete_preview", delete).Destructive);
        await server.ToolAsync(key, "oadm_recipients_delete_run", new JsonObject { ["handle"] = delete["handle"]!.GetValue<string>() });
        Assert.Empty((await Events(server).ListRecipientsAsync(new Proto.Empty())).Recipients);
        Assert.Contains(await AuditAsync(server), e => e.Action == AuditActions.RecipientDeleted);
    }

    [Fact]
    public async Task ARecipientsPasswordAndHeaderValueComeThroughTheSecretLinksAndAChangeKeepsTheRest()
    {
        const string Token = "canary-recipient-token-4Kd";
        const string Header = "hdr-canary-value-8Mw";
        await using var server = await StartWithActionsAsync();
        var key = await server.CreateKeyAsync(name: "Ops-bot");
        var answers = new System.Text.StringBuilder();

        var wrong = await server.CallToolAsync(key, "oadm_recipients_save_preview", new JsonObject
        {
            ["name"] = "Ops", ["type_id"] = "test.host.relay-type", ["settings"] = new JsonObject { ["url"] = "https://relay.example.com/a" },
            ["set_secrets"] = new JsonArray(new JsonObject { ["field"] = "url" }, new JsonObject { ["field"] = "headers" }),
        });
        Assert.Equal(["set_secrets[0]", "set_secrets[1]"], JsonNode.Parse(Text(wrong))!["errors"]!.AsArray().Select(e => e!["field"]!.GetValue<string>()));

        var arguments = new JsonObject
        {
            ["name"] = "Ops", ["type_id"] = "test.host.relay-type", ["settings"] = new JsonObject { ["url"] = "https://relay.example.com/a" },
            ["set_secrets"] = new JsonArray(new JsonObject { ["field"] = "token" }, new JsonObject { ["field"] = "headers", ["header"] = "Authorization" }),
        };
        var preview = await server.ToolAsync(key, "oadm_recipients_save_preview", arguments);
        answers.Append(preview.ToJsonString());
        Assert.Equal(["token", "header"], preview["needs_secrets"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(["token", "value of the header Authorization"], preview["secrets"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Contains(Card(server, "oadm_recipients_save_preview", preview, arguments).Details, d => d.Contains("value of the header Authorization", StringComparison.Ordinal));
        Assert.Empty((await Events(server).ListRecipientsAsync(new Proto.Empty())).Recipients); // nothing saved

        var handle = preview["handle"]!.GetValue<string>();
        var early = await server.CallToolAsync(key, "oadm_recipients_save_run", new JsonObject { ["handle"] = handle });
        Assert.Contains("Enter the token.", Text(early), StringComparison.Ordinal);
        foreach (var (slot, value) in new[] { ("token", Token), ("header", Header) })
        {
            using var client = server.Http();
            using var put = new HttpRequestMessage(HttpMethod.Put, new Uri(preview["secret_links"]![slot]!["url"]!.GetValue<string>()).PathAndQuery)
            {
                Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(value)),
            };
            put.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            using var sent = await client.SendAsync(put);
            Assert.Equal(System.Net.HttpStatusCode.OK, sent.StatusCode);
            answers.Append(await sent.Content.ReadAsStringAsync());
        }

        var saved = await server.ToolAsync(key, "oadm_recipients_save_run", new JsonObject { ["handle"] = handle });
        answers.Append(saved.ToJsonString());
        var store = server.Host.Get<RecipientStore>();
        var stored = store.DecryptSettings((await store.GetAsync(Guid.Parse(saved["recipient_id"]!.GetValue<string>()), CancellationToken.None))!);
        Assert.Equal(Token, stored["token"]!.GetValue<string>());
        Assert.Equal(Header, stored["headers"]![0]!["value"]!.GetValue<string>());

        // A new target needs the password again, as in the dialog: the preview asks the user for it.
        var moved = await server.ToolAsync(key, "oadm_recipients_save_preview", new JsonObject
        {
            ["recipient_id"] = saved["recipient_id"]!.GetValue<string>(), ["settings"] = new JsonObject { ["url"] = "https://relay.example.com/b" },
        });
        Assert.Equal(["token"], moved["needs_secrets"]!.AsArray().Select(n => n!.GetValue<string>()));

        // A change sets only what it names: the stored token and header stay.
        var change = await server.ToolAsync(key, "oadm_recipients_save_preview", new JsonObject
        {
            ["recipient_id"] = saved["recipient_id"]!.GetValue<string>(), ["name"] = "Ops desk",
        });
        Assert.True(change["has_password"]!.GetValue<bool>());
        Assert.Null(change["needs_secrets"]);
        await server.ToolAsync(key, "oadm_recipients_save_run", new JsonObject { ["handle"] = change["handle"]!.GetValue<string>() });
        var after = store.DecryptSettings((await store.GetAsync(Guid.Parse(saved["recipient_id"]!.GetValue<string>()), CancellationToken.None))!);
        Assert.Equal(Token, after["token"]!.GetValue<string>());
        Assert.Equal(Header, after["headers"]![0]!["value"]!.GetValue<string>());

        Assert.Contains(await AuditAsync(server), e => e.Action == AuditActions.SecretSet && e.Target == "value of the header Authorization" && e.Detail == "For Save a recipient");
        await SecretScan.AssertNowhereAsync(server.Host, Token, answers.ToString()); // the token is stored encrypted
        await SecretScan.AssertNowhereAsync(server.Host, Header, answers.ToString(), "oadm.db", "oadm.db-wal", "oadm.db-shm"); // a header is stored as the dialog stores it
    }

    [Fact]
    public async Task ComplianceIsTurnedOnChecksAreSavedTurnedOffAndDeletedAndTheFailingDevicesComeAPageAtATime()
    {
        await using var server = await McpTestServer.StartAsync();
        var devices = new List<Oadm.Core.Devices.Device>();
        for (var i = 1; i <= 3; i++)
        {
            devices.Add(await DeviceServiceTests.AddDeviceAsync(server.Host, $"10.66.0.{i}", 9600 + i, status: i == 3 ? Oadm.Sdk.Devices.DeviceStatus.Ok : Oadm.Sdk.Devices.DeviceStatus.Unreachable));
        }

        var compliance = new Proto.ComplianceService.ComplianceServiceClient(server.Host.Invoker);
        var key = await server.CreateKeyAsync();

        var on = await server.ToolAsync(key, "oadm_compliance_enable_preview", new JsonObject { ["enabled"] = true });
        Assert.False((await compliance.ListChecksAsync(new Proto.Empty())).Enabled); // nothing written
        Card(server, "oadm_compliance_enable_preview", on);
        await server.ToolAsync(key, "oadm_compliance_enable_run", new JsonObject { ["handle"] = on["handle"]!.GetValue<string>() });
        Assert.True((await compliance.ListChecksAsync(new Proto.Empty())).Enabled);
        Assert.Contains(await AuditAsync(server), e => e.Action == AuditActions.ComplianceTurnedOn);
        await Eventually.TrueAsync(async () =>
        {
            var reachable = (await compliance.ListChecksAsync(new Proto.Empty())).Checks.Single(c => c.Name == "Reachable");
            return reachable.Passed + reachable.Failed == 3;
        }, "a result of the Reachable check on every device");

        var reachableId = (await compliance.ListChecksAsync(new Proto.Empty())).Checks.Single(c => c.Name == "Reachable").Id;
        var first = await server.ToolAsync(key, "oadm_compliance_devices", new JsonObject { ["check_id"] = reachableId, ["limit"] = 1 });
        Assert.Equal(2, first["total"]!.GetValue<int>());
        Assert.Single(first["devices"]!.AsArray());
        Assert.Equal("fail", first["devices"]![0]!["result"]!.GetValue<string>());
        var second = await server.ToolAsync(key, "oadm_compliance_devices", new JsonObject { ["check_id"] = reachableId, ["limit"] = 1, ["cursor"] = first["next_cursor"]!.GetValue<string>() });
        Assert.NotEqual(first["devices"]![0]!["device"]!["id"]!.GetValue<string>(), second["devices"]![0]!["device"]!["id"]!.GetValue<string>());
        Assert.Null(second["next_cursor"]);
        var passing = await server.ToolAsync(key, "oadm_compliance_devices", new JsonObject { ["check_id"] = reachableId, ["outcomes"] = new JsonArray("pass") });
        Assert.Equal(devices[2].Id.ToString(), passing["devices"]![0]!["device"]!["id"]!.GetValue<string>());

        var builtIn = await server.CallToolAsync(key, "oadm_compliance_check_delete_preview", new JsonObject { ["check_id"] = reachableId });
        Assert.True(builtIn["isError"]!.GetValue<bool>());
        Assert.Contains(ComplianceChangeTools.BuiltInCheck, Text(builtIn), StringComparison.Ordinal);

        var create = await server.ToolAsync(key, "oadm_compliance_check_save_preview", new JsonObject
        {
            ["name"] = "Old firmware", ["type_id"] = Core.Compliance.BuiltInComplianceTypes.FirmwareId, ["devices"] = new JsonObject { ["tags"] = new JsonArray("Lobby") },
            ["settings"] = new JsonObject { ["minimum"] = "12.0" },
        });
        Assert.Equal("create", create["change"]!.GetValue<string>());
        var before = (await compliance.ListChecksAsync(new Proto.Empty())).Checks.Count;
        Assert.Equal("1 check", Card(server, "oadm_compliance_check_save_preview", create).CountText);
        var saved = await server.ToolAsync(key, "oadm_compliance_check_save_run", new JsonObject { ["handle"] = create["handle"]!.GetValue<string>() });
        Assert.Equal(before + 1, (await compliance.ListChecksAsync(new Proto.Empty())).Checks.Count);
        var checkId = saved["check_id"]!.GetValue<string>();
        Assert.Contains(await AuditAsync(server), e => e.Action == AuditActions.ComplianceCheckCreated && e.Target == "Old firmware");

        var rename = await server.ToolAsync(key, "oadm_compliance_check_save_preview", new JsonObject { ["check_id"] = checkId, ["name"] = "AXIS OS too old" });
        Assert.Equal("change", rename["change"]!.GetValue<string>());
        await server.ToolAsync(key, "oadm_compliance_check_save_run", new JsonObject { ["handle"] = rename["handle"]!.GetValue<string>() });
        Assert.Contains((await compliance.ListChecksAsync(new Proto.Empty())).Checks, c => c.Name == "AXIS OS too old");

        var off = await server.ToolAsync(key, "oadm_compliance_check_enable_preview", new JsonObject { ["check_id"] = checkId, ["enabled"] = false });
        Card(server, "oadm_compliance_check_enable_preview", off);
        await server.ToolAsync(key, "oadm_compliance_check_enable_run", new JsonObject { ["handle"] = off["handle"]!.GetValue<string>() });
        Assert.False((await compliance.ListChecksAsync(new Proto.Empty())).Checks.Single(c => c.Id == checkId).Enabled);

        var now = await server.ToolAsync(key, "oadm_compliance_check_now_preview", new JsonObject { ["check_ids"] = new JsonArray(reachableId) });
        Assert.Equal(3, now["devices"]!.GetValue<int>());
        Assert.Equal("3 devices", Card(server, "oadm_compliance_check_now_preview", now).CountText);
        Assert.True((await server.ToolAsync(key, "oadm_compliance_check_now_run", new JsonObject { ["handle"] = now["handle"]!.GetValue<string>() }))["started"]!.GetValue<bool>());

        var delete = await server.ToolAsync(key, "oadm_compliance_check_delete_preview", new JsonObject { ["check_id"] = checkId });
        Assert.True(Card(server, "oadm_compliance_check_delete_preview", delete).Destructive);
        await server.ToolAsync(key, "oadm_compliance_check_delete_run", new JsonObject { ["handle"] = delete["handle"]!.GetValue<string>() });
        Assert.DoesNotContain((await compliance.ListChecksAsync(new Proto.Empty())).Checks, c => c.Id == checkId);
        Assert.Contains(await AuditAsync(server), e => e.Action == AuditActions.ComplianceCheckDeleted);
        await AssertOnceAsync(server, key, "oadm_compliance_check_delete_run", delete["handle"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheCameraControlsCheckTheCameraFirstAndTheRtspPortIsStoredOnlyByTheRun()
    {
        await using var server = await McpTestServer.StartAsync();
        var device = await DeviceServiceTests.AddDeviceAsync(server.Host, "10.67.0.1", 9701);
        var key = await server.CreateKeyAsync();
        var id = device.Id.ToString();

        // A camera without a remote lens or PTZ: nothing is sent to it.
        var optics = await server.ToolAsync(key, "oadm_devices_optics", new JsonObject { ["device_id"] = id });
        Assert.False(optics["supported"]!.GetValue<bool>());
        var noLens = await server.CallToolAsync(key, "oadm_devices_optics_preview", new JsonObject { ["device_id"] = id, ["autofocus"] = true });
        Assert.True(noLens["isError"]!.GetValue<bool>());
        Assert.Contains(LiveViewTools.NoLens, Text(noLens), StringComparison.Ordinal);
        var two = await server.CallToolAsync(key, "oadm_devices_optics_preview", new JsonObject { ["device_id"] = id, ["autofocus"] = true, ["focus"] = 0.5 });
        Assert.Contains(LiveViewTools.OneOpticsChange, Text(two), StringComparison.Ordinal);
        var noPtz = await server.CallToolAsync(key, "oadm_devices_ptz_preview", new JsonObject { ["device_id"] = id, ["move"] = "home" });
        Assert.True(noPtz["isError"]!.GetValue<bool>());
        Assert.Contains(LiveViewTools.NoPtz, Text(noPtz), StringComparison.Ordinal);

        var port = await server.ToolAsync(key, "oadm_devices_rtsp_port_preview", new JsonObject { ["filter"] = new JsonObject { ["ids"] = new JsonArray(id) }, ["port"] = 8554 });
        Assert.Equal(554, port["port_now"]!.GetValue<int>());
        Assert.Equal(554, (await server.Host.LiveView.GetRtspPortAsync(new Proto.RtspPortRequest { DeviceIds = { id } })).Port); // nothing written
        Assert.Equal("1 device", Card(server, "oadm_devices_rtsp_port_preview", port).CountText);
        var set = await server.ToolAsync(key, "oadm_devices_rtsp_port_run", new JsonObject { ["handle"] = port["handle"]!.GetValue<string>() });
        Assert.Equal(8554, set["port"]!.GetValue<int>());
        Assert.Equal(8554, (await server.Host.LiveView.GetRtspPortAsync(new Proto.RtspPortRequest { DeviceIds = { id } })).Port);
        await AssertOnceAsync(server, key, "oadm_devices_rtsp_port_run", port["handle"]!.GetValue<string>());
    }

    [Fact]
    public async Task ASiteRenameNeedsAKnownSiteTheVulnerabilityFetchNeedsTheServerModeAndTheVersionCheckReads()
    {
        await using var server = await McpTestServer.StartAsync();
        var key = await server.CreateKeyAsync();
        var site = await server.CallToolAsync(key, "oadm_sites_rename_preview", new JsonObject { ["site_id"] = Guid.NewGuid().ToString(), ["name"] = "Parking" });
        Assert.True(site["isError"]!.GetValue<bool>());
        Assert.Contains(HostActionTools.UnknownSite, Text(site), StringComparison.Ordinal);

        var vulnerabilities = await server.ToolAsync(key, "oadm_vulnerabilities_update_preview");
        Assert.NotEqual("server", vulnerabilities["update_online"]!.GetValue<string>());
        Assert.Null(vulnerabilities["handle"]);
        Assert.Contains(Oadm.Server.Vulnerabilities.VulnerabilityGrpcService.RefreshOffText, vulnerabilities["warnings"]!.ToJsonString(), StringComparison.Ordinal);

        var update = await server.ToolAsync(key, "oadm_update_check");
        Assert.False(string.IsNullOrEmpty(update["server_version"]!.GetValue<string>()));
    }

    [Fact]
    public async Task AHandleOfTheseToolsEndsAfterTenMinutesAndBelongsToItsCaller()
    {
        await using var host = await TestServerHost.StartAsync();
        await host.Tags.CreateAsync(new Proto.CreateTagRequest { Name = "Lobby" });
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var handles = new ToolHandles(time);
        var registry = new ToolRegistry([.. TagTools.Create(host.Get<ToolCursors>(), handles), .. TaskChangeTools.Create(handles)], host.Get<Oadm.Server.Auth.InProcessCalls>());
        var admin = (await host.Get<UserStore>().FindByNameAsync(TestServerHost.AdminUserName, CancellationToken.None))!;
        var permissions = await host.Get<RoleStore>().PermissionsOfAsync(admin.RoleId, CancellationToken.None);
        var devices = await host.Get<Oadm.Core.DeviceGroups.DeviceViewFactory>().CallerDevicesAsync(new DeviceScope(true, null), CancellationToken.None);
        ToolCaller Caller(string binding) => new(new AuthenticatedCaller(admin.Id, admin.UserName, admin.RoleId, permissions, "127.0.0.1", "Assistant", binding) { Devices = devices }, binding);
        static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

        var preview = await registry.CallAsync(Caller("one"), "oadm_tags_delete_preview", Args("""{"tag": "Lobby"}"""), CancellationToken.None);
        var handle = preview.Answer!["handle"]!.GetValue<string>();
        var other = await registry.CallAsync(Caller("two"), "oadm_tags_delete_run", Args($$"""{"handle": "{{handle}}"}"""), CancellationToken.None);
        Assert.Equal(AreaListing.PreviewEnded, other.Error); // another caller's handle answers like an ended one
        time.Advance(ToolHandles.Lifetime);
        var late = await registry.CallAsync(Caller("one"), "oadm_tags_delete_run", Args($$"""{"handle": "{{handle}}"}"""), CancellationToken.None);
        Assert.Equal(AreaListing.PreviewEnded, late.Error);
        Assert.Contains((await host.Tags.ListAsync(new Proto.Empty())).Tags, t => t.Name == "Lobby"); // nothing was deleted
    }

    [Fact]
    public async Task DiscoveryInTheAddPreviewOnlyLooksAndPlansTheDevicesThatAnnounceThemselves()
    {
        var network = new FakeAxisNetwork();
        network.Add("10.68.0.5", FakeSerials.Make(9805), "pass");
        await using var server = await McpTestServer.StartAsync(network, configureServices: services =>
        {
            services.RemoveAll<Oadm.Core.Discovery.Mdns.IMdnsBrowser>();
            services.AddSingleton<Oadm.Core.Discovery.Mdns.IMdnsBrowser>(new OneDevice("10.68.0.5", FakeSerials.Make(9805)));
        });
        await server.Host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "root", Password = "wrong-one" });
        var key = await server.CreateKeyAsync();

        var both = await server.CallToolAsync(key, "oadm_devices_add_preview", new JsonObject { ["discovery"] = true, ["addresses"] = new JsonArray("10.68.0.5") });
        Assert.True(both["isError"]!.GetValue<bool>());
        Assert.Contains(DeviceChangeTools.ExactlyOne, Text(both), StringComparison.Ordinal);

        var preview = await server.ToolAsync(key, "oadm_devices_add_preview", new JsonObject { ["discovery"] = true });
        Assert.Equal("discovery", preview["source"]!.GetValue<string>());
        Assert.True(preview["complete"]!.GetValue<bool>());
        Assert.Equal(1, preview["found"]!.GetValue<int>());
        Assert.Equal(1, preview["will_try"]!.GetValue<int>());
        Assert.Equal(0, network["10.68.0.5"].RejectedLogins); // it only looked: no login, not even a wrong one
        Assert.Empty((await server.Host.Devices.ListAsync(new Proto.Empty())).Devices); // nothing stored
        Assert.Equal("1 device", Card(server, "oadm_devices_add_preview", preview).CountText);
    }

    /// <summary>An mDNS browser that announces one device, then ends (the scan finishes at once).</summary>
    private sealed class OneDevice(string address, string serial) : Oadm.Core.Discovery.Mdns.IMdnsBrowser
    {
        public async IAsyncEnumerable<Oadm.Core.Discovery.Mdns.MdnsServiceInstance> BrowseAsync(Oadm.Core.Discovery.Mdns.MdnsBrowseOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new Oadm.Core.Discovery.Mdns.MdnsServiceInstance("AXIS M3085 - " + serial, "axis-" + serial.ToLowerInvariant() + ".local", 80,
                [System.Net.IPAddress.Parse(address)], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["macaddress"] = serial }, serial, null);
        }
    }

    private sealed class NotifyAction : IActionPlugin
    {
        private static int _runs;

        public static int Runs => Volatile.Read(ref _runs);

        public static string? LastToken { get; private set; }

        public string Id => "test.host.notify";

        public string DisplayName => "Notify";

        public string? IconKey => null;

        public bool NeedsDevice => false;

        public IReadOnlyList<ActionField> Fields { get; } = [ActionField.Text("message", "Message", required: true), ActionField.Password("token", "Token")];

        public Task ExecuteAsync(IActionContext ctx, ActionInvocation invocation, CancellationToken ct)
        {
            LastToken = invocation.Settings.TryGetProperty("token", out var token) ? token.GetString() : null;
            Interlocked.Increment(ref _runs);
            return Task.CompletedTask;
        }
    }

    private sealed class RelayAction : IActionPlugin
    {
        public string Id => "test.host.relay";

        public string DisplayName => "Relay";

        public string? IconKey => null;

        public bool NeedsDevice => false;

        public IReadOnlyList<RecipientType> RecipientTypes { get; } =
        [
            new("test.host.relay-type", "Relay", null,
                [ActionField.Text("url", "URL", required: true) with { Format = ActionFieldFormat.Url }, ActionField.Password("token", "Token"), ActionField.KeyValues("headers", "Headers")],
                "test.host.relay") { Target = "{url}" },
        ];

        public IReadOnlyList<ActionField> Fields { get; } = [ActionField.Recipient("recipient", "Recipient", ["test.host.relay-type"])];

        public Task ExecuteAsync(IActionContext ctx, ActionInvocation invocation, CancellationToken ct) => Task.CompletedTask;
    }
}
