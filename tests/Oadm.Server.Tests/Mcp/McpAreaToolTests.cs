using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Oadm.Core.Auth;
using Oadm.Core.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;
using Oadm.Server.Mcp;
using Oadm.Server.Sites;
using Oadm.Server.Tests.Sites;
using Oadm.Server.Tests.Support;
using Oadm.Server.Tools;
using Oadm.SiteNode.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Mcp;

/// <summary>
/// The tools of the areas tags, sites and compliance (docs/spec/mcp.md "Other areas"): the lists with totals and cursors, the
/// tag preview that writes nothing and the run that takes only its handle, the caller's device group, what a site node keeps
/// secret, and the compliance results per check for the devices of a filter. The event rules are in <see cref="McpRuleToolTests"/>.
/// </summary>
public sealed class McpAreaToolTests
{
    /// <summary>The tools of the areas (<see cref="McpToolTests"/> lists them with those of the other stages).</summary>
    public static readonly string[] ToolNames =
    [
        "oadm_compliance_checks", "oadm_compliance_summary", "oadm_events_log", "oadm_rules_catalog", "oadm_rules_delete_preview",
        "oadm_rules_delete_run", "oadm_rules_get", "oadm_rules_list", "oadm_rules_save_preview", "oadm_rules_save_run", "oadm_sites_list",
        "oadm_tags_assign_preview", "oadm_tags_assign_run", "oadm_tags_list",
    ];

    /// <summary>The tools that change something: each takes only the handle of its preview.</summary>
    public static readonly string[] RunTools = ["oadm_rules_delete_run", "oadm_rules_save_run", "oadm_tags_assign_run"];

    /// <summary>The gRPC methods the tools of the areas call.</summary>
    public static readonly string[] Methods =
    [
        "/oadm.v1.ComplianceService/GetCatalog", "/oadm.v1.ComplianceService/ListChecks", "/oadm.v1.ComplianceService/Summarize",
        "/oadm.v1.DeviceService/Search",
        "/oadm.v1.EventService/CheckRule", "/oadm.v1.EventService/DeleteRule", "/oadm.v1.EventService/GetCatalog", "/oadm.v1.EventService/GetRule",
        "/oadm.v1.EventService/ListLog", "/oadm.v1.EventService/ListRecipients", "/oadm.v1.EventService/ListRules", "/oadm.v1.EventService/SaveRule",
        "/oadm.v1.SiteService/ListSites",
        "/oadm.v1.TagService/List", "/oadm.v1.TagService/PlanDeviceTags", "/oadm.v1.TagService/SetDeviceTags",
    ];

    /// <summary>One call of every tool of the areas for the scan of <see cref="McpSecretTests"/> (no secret in any answer).</summary>
    public static Dictionary<string, JsonObject> SecretScanCalls() => new()
    {
        ["oadm_tags_list"] = [],
        ["oadm_tags_assign_preview"] = new() { ["add"] = new JsonArray("Scan") },
        ["oadm_tags_assign_run"] = new() { ["handle"] = "ended" },
        ["oadm_sites_list"] = [],
        ["oadm_compliance_summary"] = [],
        ["oadm_compliance_checks"] = [],
        ["oadm_rules_catalog"] = [],
        ["oadm_rules_list"] = [],
        ["oadm_rules_get"] = new() { ["rule_id"] = Guid.NewGuid().ToString() },
        ["oadm_rules_save_preview"] = new() { ["rule"] = new JsonObject { ["name"] = "Scan", ["conditions"] = new JsonArray(), ["actions"] = new JsonArray() } },
        ["oadm_rules_save_run"] = new() { ["handle"] = "ended" },
        ["oadm_rules_delete_preview"] = new() { ["rule_id"] = Guid.NewGuid().ToString() },
        ["oadm_rules_delete_run"] = new() { ["handle"] = "ended" },
        ["oadm_events_log"] = [],
    };

    private static async Task<(McpTestServer Server, Device[] Devices)> StartWithDevicesAsync(int count, string prefix, int serial)
    {
        var server = await McpTestServer.StartAsync();
        var devices = new List<Device>();
        for (var i = 1; i <= count; i++)
        {
            devices.Add(await DeviceServiceTests.AddDeviceAsync(server.Host, $"{prefix}.{i}", serial + i));
        }

        return (server, [.. devices]);
    }

    private static string Text(JsonObject result) => result["content"]![0]!["text"]!.GetValue<string>();

    private static IEnumerable<string> Names(JsonNode? array) => array!.AsArray().Select(t => t!["name"]!.GetValue<string>());

    [Fact]
    public async Task TheTagListCountsDevicesInTheCallersGroupPagesAndRefusesAnotherKeysCursor()
    {
        var (server, devices) = await StartWithDevicesAsync(4, "10.21.0", 7100);
        await using var _ = server;
        await server.Host.Tags.SetDeviceTagsAsync(new Proto.SetDeviceTagsRequest { DeviceIds = { devices[0].Id.ToString(), devices[1].Id.ToString(), devices[2].Id.ToString() }, Add = { "Lobby", "Alpha" } });
        await server.Host.Tags.SetDeviceTagsAsync(new Proto.SetDeviceTagsRequest { DeviceIds = { devices[3].Id.ToString() }, Add = { "Garage" } });
        var key = await server.CreateKeyAsync();

        var all = await server.ToolAsync(key, "oadm_tags_list");
        Assert.Equal(3, all["total"]!.GetValue<int>());
        Assert.Equal(["Alpha", "Garage", "Lobby"], Names(all["tags"]));
        Assert.Equal(3, all["tags"]![0]!["devices"]!.GetValue<int>());
        Assert.Equal(1, all["tags"]![1]!["devices"]!.GetValue<int>());
        Assert.True(all["tags"]![0]!["defined"]!.GetValue<bool>());
        Assert.Null(all["next_cursor"]);

        var first = await server.ToolAsync(key, "oadm_tags_list", new JsonObject { ["limit"] = 2 });
        Assert.Equal(["Alpha", "Garage"], Names(first["tags"]));
        Assert.Equal(3, first["total"]!.GetValue<int>());
        var cursor = first["next_cursor"]!.GetValue<string>();
        await server.Host.Tags.CreateAsync(new Proto.CreateTagRequest { Name = "Beta" }); // a tag added elsewhere never moves the page
        var second = await server.ToolAsync(key, "oadm_tags_list", new JsonObject { ["limit"] = 2, ["cursor"] = cursor });
        Assert.Equal(["Lobby"], Names(second["tags"]));
        var other = await server.CreateKeyAsync(name: "other");
        var foreign = await server.CallToolAsync(other, "oadm_tags_list", new JsonObject { ["limit"] = 2, ["cursor"] = cursor });
        Assert.True(foreign["isError"]!.GetValue<bool>());
        Assert.Contains(ToolCursors.NotValid, Text(foreign), StringComparison.Ordinal);

        var found = await server.ToolAsync(key, "oadm_tags_list", new JsonObject { ["search"] = "gar" });
        Assert.Equal(["Garage"], Names(found["tags"]));

        // A user of a device group counts the devices of the group only.
        var group = await GroupCallers.ForTagAsync(server.Host, "tech", "Lobby");
        var groupKey = await server.CreateKeyAsync(group.Invoker);
        var mine = await server.ToolAsync(groupKey, "oadm_tags_list");
        Assert.Equal(3, mine["tags"]!.AsArray().Single(t => t!["name"]!.GetValue<string>() == "Lobby")!["devices"]!.GetValue<int>());
        Assert.Equal(0, mine["tags"]!.AsArray().Single(t => t!["name"]!.GetValue<string>() == "Garage")!["devices"]!.GetValue<int>());
    }

    [Fact]
    public async Task ATagPreviewWritesNothingAndTheRunTakesOnlyItsHandleOnceAndOnlyAsItsOwnRunTool()
    {
        var (server, devices) = await StartWithDevicesAsync(3, "10.22.0", 7200);
        await using var _ = server;
        await server.Host.Tags.SetDeviceTagsAsync(new Proto.SetDeviceTagsRequest { DeviceIds = { devices[0].Id.ToString() }, Add = { "Old" } });
        var key = await server.CreateKeyAsync();
        static JsonObject Filter() => new() { ["search"] = "10.22.0." };

        var preview = await server.ToolAsync(key, "oadm_tags_assign_preview", new JsonObject { ["filter"] = Filter(), ["add"] = new JsonArray("New"), ["remove"] = new JsonArray("Old") });
        Assert.Equal(3, preview["devices"]!.GetValue<int>());
        Assert.Equal(3, preview["will_change"]!.GetValue<int>());
        Assert.Equal(3, preview["gain"]!.GetValue<int>());
        Assert.Equal(1, preview["lose"]!.GetValue<int>());
        Assert.True(preview["destructive"]!.GetValue<bool>());
        Assert.Equal(["New"], preview["new_tags"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.Equal(3, preview["first"]!.AsArray().Count);
        Assert.NotEmpty(preview["warnings"]!.AsArray());
        Assert.Equal(["Old"], (await server.Host.Get<DeviceRepository>().GetAsync(devices[0].Id, CancellationToken.None))!.Tags); // nothing written
        Assert.DoesNotContain((await server.Host.Tags.ListAsync(new Proto.Empty())).Tags, t => t.Name == "New");

        // A handle belongs to its key and to its own run tool.
        var handle = preview["handle"]!.GetValue<string>();
        var other = await server.CreateKeyAsync(name: "other");
        Assert.True((await server.CallToolAsync(other, "oadm_tags_assign_run", new JsonObject { ["handle"] = handle }))["isError"]!.GetValue<bool>());
        Assert.True((await server.CallToolAsync(key, "oadm_task_run", new JsonObject { ["handle"] = handle }))["isError"]!.GetValue<bool>());
        Assert.True((await server.CallToolAsync(key, "oadm_rules_delete_run", new JsonObject { ["handle"] = handle }))["isError"]!.GetValue<bool>());

        var run = await server.ToolAsync(key, "oadm_tags_assign_run", new JsonObject { ["handle"] = handle });
        Assert.Equal(3, run["devices_changed"]!.GetValue<int>());
        Assert.Equal(["New"], run["created"]!.AsArray().Select(t => t!.GetValue<string>()));
        foreach (var device in devices)
        {
            Assert.Equal(["New"], (await server.Host.Get<DeviceRepository>().GetAsync(device.Id, CancellationToken.None))!.Tags);
        }

        var again = await server.CallToolAsync(key, "oadm_tags_assign_run", new JsonObject { ["handle"] = handle });
        Assert.True(again["isError"]!.GetValue<bool>());
        Assert.Contains(AreaListing.PreviewEnded, Text(again), StringComparison.Ordinal);
        var audit = (await server.Host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        Assert.Contains(audit, e => e.Action == AuditActions.DevicesTagged && e.UserName == AuditActions.ViaAccessKey(TestServerHost.AdminUserName, "agent"));

        // Nothing left to change: no handle.
        var done = await server.ToolAsync(key, "oadm_tags_assign_preview", new JsonObject { ["filter"] = Filter(), ["add"] = new JsonArray("New") });
        Assert.Equal(0, done["will_change"]!.GetValue<int>());
        Assert.Null(done["handle"]);
        Assert.Contains(TagTools.NothingToChange, done["warnings"]!.AsArray().Select(w => w!.GetValue<string>()));
    }

    [Fact]
    public async Task AddingTagsIsNotFlaggedDestructiveAndBadInputIsRefusedWithFieldErrors()
    {
        var (server, devices) = await StartWithDevicesAsync(2, "10.23.0", 7300);
        await using var _ = server;
        var key = await server.CreateKeyAsync();

        var adding = await server.ToolAsync(key, "oadm_tags_assign_preview", new JsonObject { ["add"] = new JsonArray("Free") });
        Assert.False(adding["destructive"]!.GetValue<bool>());
        Assert.Equal(2, adding["will_change"]!.GetValue<int>());

        var none = await server.CallToolAsync(key, "oadm_tags_assign_preview", new JsonObject());
        Assert.True(none["isError"]!.GetValue<bool>());
        Assert.Contains(TagTools.NoTags, Text(none), StringComparison.Ordinal);
        var bad = await server.CallToolAsync(key, "oadm_tags_assign_preview", new JsonObject { ["add"] = new JsonArray("a;b") });
        Assert.True(bad["isError"]!.GetValue<bool>());
        var both = await server.CallToolAsync(key, "oadm_tags_assign_preview", new JsonObject { ["add"] = new JsonArray("Same"), ["remove"] = new JsonArray("same") });
        Assert.True(both["isError"]!.GetValue<bool>());
        var tooMany = await server.CallToolAsync(key, "oadm_tags_assign_preview", new JsonObject { ["add"] = new JsonArray([.. Enumerable.Range(0, 21).Select(i => (JsonNode)$"t{i}")]) });
        Assert.True(tooMany["isError"]!.GetValue<bool>());
        Assert.DoesNotContain((await server.Host.Tags.ListAsync(new Proto.Empty())).Tags, t => t.Name != "Free" && t.DeviceCount > 0);
        Assert.Equal(2, devices.Length);
    }

    [Fact]
    public async Task ADeviceOutsideTheGroupAnswersLikeAnUnknownOneAndOnlyTheGroupsDevicesAreTagged()
    {
        var (server, devices) = await StartWithDevicesAsync(4, "10.24.0", 7400);
        await using var _ = server;
        await server.Host.Tags.SetDeviceTagsAsync(new Proto.SetDeviceTagsRequest { DeviceIds = { devices[1].Id.ToString(), devices[3].Id.ToString() }, Add = { "Lobby" } });
        var group = await GroupCallers.ForTagAsync(server.Host, "tech", "Lobby");
        var key = await server.CreateKeyAsync(group.Invoker);

        var outside = await server.CallToolAsync(key, "oadm_tags_assign_preview", new JsonObject
        {
            ["filter"] = new JsonObject { ["ids"] = new JsonArray(devices[0].Id.ToString()) },
            ["add"] = new JsonArray("Free"),
        });
        Assert.True(outside["isError"]!.GetValue<bool>());
        Assert.Contains("1 device is no longer available.", Text(outside), StringComparison.Ordinal);

        var preview = await server.ToolAsync(key, "oadm_tags_assign_preview", new JsonObject { ["add"] = new JsonArray("Free") });
        Assert.Equal(2, preview["devices"]!.GetValue<int>());
        Assert.Equal(2, preview["will_change"]!.GetValue<int>());
        Assert.Equal(2, (await server.ToolAsync(key, "oadm_tags_assign_run", new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() }))["devices_changed"]!.GetValue<int>());
        Assert.Empty((await server.Host.Get<DeviceRepository>().GetAsync(devices[0].Id, CancellationToken.None))!.Tags);
        Assert.Contains("Free", (await server.Host.Get<DeviceRepository>().GetAsync(devices[1].Id, CancellationToken.None))!.Tags);

        // The group's own tag stays the administrators' (a group user cannot remove what defines the group).
        var removal = await server.ToolAsync(key, "oadm_tags_assign_preview", new JsonObject { ["remove"] = new JsonArray("Lobby") });
        var refused = await server.CallToolAsync(key, "oadm_tags_assign_run", new JsonObject { ["handle"] = removal["handle"]!.GetValue<string>() });
        Assert.True(refused["isError"]!.GetValue<bool>());
        Assert.Contains("Lobby", (await server.Host.Get<DeviceRepository>().GetAsync(devices[1].Id, CancellationToken.None))!.Tags);
    }

    [Fact]
    public async Task AReadOnlyKeyAndARoleWithoutTheTagPermissionListNoTagChangeTool()
    {
        await using var server = await McpTestServer.StartAsync();
        var readOnly = await server.CreateKeyAsync(readOnly: true);
        var names = await server.ToolNamesAsync(readOnly);
        Assert.Contains("oadm_tags_assign_preview", names); // a preview writes nothing
        Assert.Contains("oadm_tags_list", names);
        Assert.All(RunTools, run => Assert.DoesNotContain(run, names));
        var refused = await server.CallToolAsync(readOnly, "oadm_tags_assign_run", new JsonObject { ["handle"] = "x" });
        Assert.Contains(McpAccess.ReadOnlyKey, Text(refused), StringComparison.Ordinal);

        var roles = new Proto.RoleService.RoleServiceClient(server.Host.Invoker);
        var saved = await roles.SaveRolesAsync(new Proto.SaveRolesRequest { Roles = { new Proto.RoleChange { Name = "Readers", Permissions = { HostPermissions.McpUse } } } });
        var role = saved.Roles.Single(r => r.Name == "Readers");
        var key = await server.CreateKeyAsync(await server.Host.InvokerForRoleAsync("reader", Guid.Parse(role.Id)));
        var listed = await server.ToolNamesAsync(key);
        Assert.Contains("oadm_tags_list", listed); // every signed-in user lists tags
        Assert.DoesNotContain("oadm_tags_assign_preview", listed);
        Assert.DoesNotContain("oadm_tags_assign_run", listed);
        Assert.DoesNotContain("oadm_sites_list", listed); // "See sites"
        Assert.DoesNotContain("oadm_compliance_summary", listed); // "See compliance"
        Assert.DoesNotContain("oadm_events_log", listed); // the event rules
        var denied = await server.CallToolAsync(key, "oadm_tags_assign_preview", new JsonObject { ["add"] = new JsonArray("x") });
        Assert.True(denied["isError"]!.GetValue<bool>());
        Assert.Contains("Your role does not allow", Text(denied), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSiteListShowsTheBoundSitesWithTheirStatusAndNeverWhatANodeKeepsSecret()
    {
        await using var server = await McpTestServer.StartAsync(configureServices: s =>
        {
            s.RemoveAll<SiteOptions>();
            s.AddSingleton(new SiteOptions { ListenUrl = "127.0.0.1:0", WorkerWait = TimeSpan.FromSeconds(30), CallTimeout = TimeSpan.FromSeconds(30), HandshakeTimeout = TimeSpan.FromSeconds(30) });
        });
        var manager = server.Host.Get<SiteManager>();
        await manager.SetAcceptAsync(true, CancellationToken.None);
        var sites = new Proto.SiteService.SiteServiceClient(server.Host.Invoker);
        var address = "127.0.0.1:" + manager.ListenPort;

        await using var node = await SiteTestNode.StartAsync(c => c.ServerAddress = address);
        Proto.Site? waiting = null;
        await Eventually.TrueAsync(async () => (waiting = (await sites.ListSitesAsync(new Proto.Empty())).Sites.FirstOrDefault(s => s.State == Proto.SiteState.Waiting && s.NodeId == node.NodeId)) is not null, "the node waits");
        var key = await server.CreateKeyAsync();

        var before = await server.ToolAsync(key, "oadm_sites_list");
        Assert.Equal(0, before["total"]!.GetValue<int>());
        Assert.Equal(1, before["waiting_nodes"]!.GetValue<int>()); // counted, never listed
        Assert.True(before["accept_nodes"]!.GetValue<bool>());

        var bound = await sites.BindSiteAsync(new Proto.BindSiteRequest { NodeId = waiting!.NodeId, NodeFingerprint = waiting.NodeFingerprint, Name = "Building B", PairingCode = node.Code });
        await Eventually.TrueAsync(() => manager.IsUsable(Guid.Parse(bound.Id)), "the bound site online");
        var site = await DeviceServiceTests.AddDeviceAsync(server.Host, "10.25.0.1", 7500);
        await server.Host.Get<DeviceRepository>().UpdateAsync(site.Id, d => d.SiteId = Guid.Parse(bound.Id), CancellationToken.None);

        var list = await server.CallToolAsync(key, "oadm_sites_list");
        var answer = list["structuredContent"]!.AsObject();
        Assert.Equal(1, answer["total"]!.GetValue<int>());
        var row = Assert.Single(answer["sites"]!.AsArray())!.AsObject();
        Assert.Equal("Building B", row["name"]!.GetValue<string>());
        Assert.Equal("Online", row["status"]!.GetValue<string>());
        Assert.Equal(bound.Id, row["id"]!.GetValue<string>());
        Assert.Equal(1, row["devices"]!.GetValue<int>());
        Assert.Equal(0, answer["waiting_nodes"]!.GetValue<int>());

        // Nothing the node keeps secret: its id, its key's fingerprint, its address and its pairing code.
        var text = Text(list);
        Assert.DoesNotContain(node.NodeId, text, StringComparison.Ordinal);
        Assert.DoesNotContain(node.Code, text, StringComparison.Ordinal);
        Assert.DoesNotContain(bound.NodeFingerprint, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(bound.NodeAddress, text, StringComparison.Ordinal);
        Assert.False(row.ContainsKey("node_id") || row.ContainsKey("node_address") || row.ContainsKey("node_fingerprint"));
    }

    [Fact]
    public async Task ComplianceResultsCountPassFailAndUnknownPerCheckForTheFiltersDevicesTheWorstFirst()
    {
        var (server, devices) = await StartWithDevicesAsync(3, "10.26.0", 7600);
        await using var _ = server;
        var unreachable = await DeviceServiceTests.AddDeviceAsync(server.Host, "10.26.0.9", 7690, status: Oadm.Sdk.Devices.DeviceStatus.Unreachable);
        var compliance = new Proto.ComplianceService.ComplianceServiceClient(server.Host.Invoker);
        var key = await server.CreateKeyAsync();

        var off = await server.ToolAsync(key, "oadm_compliance_summary");
        Assert.False(off["compliance_on"]!.GetValue<bool>());
        Assert.Contains(ComplianceTools.IsOff, off["note"]!.GetValue<string>(), StringComparison.Ordinal);

        await compliance.SetEnabledAsync(new Proto.SetComplianceEnabledRequest { Enabled = true });
        await Eventually.TrueAsync(async () =>
        {
            var reachable = (await compliance.ListChecksAsync(new Proto.Empty())).Checks.Single(c => c.Name == "Reachable");
            return reachable.Passed + reachable.Failed == 4;
        }, "a result of the Reachable check on every device");

        var all = await server.ToolAsync(key, "oadm_compliance_summary");
        Assert.True(all["compliance_on"]!.GetValue<bool>());
        Assert.Equal(4, all["devices"]!.GetValue<int>());
        var reachableRow = all["checks"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == "Reachable")!.AsObject();
        Assert.Equal(3, reachableRow["pass"]!.GetValue<int>());
        Assert.Equal(1, reachableRow["fail"]!.GetValue<int>());
        Assert.Equal("Reachable", all["checks"]![0]!["name"]!.GetValue<string>()); // the check with the failure is first
        var fails = all["checks"]!.AsArray().Select(c => c!["fail"]!.GetValue<int>()).ToList();
        Assert.Equal(fails.OrderByDescending(f => f), fails);

        var filtered = await server.ToolAsync(key, "oadm_compliance_summary", new JsonObject { ["filter"] = new JsonObject { ["ids"] = new JsonArray(unreachable.Id.ToString(), devices[0].Id.ToString()) } });
        Assert.Equal(2, filtered["devices"]!.GetValue<int>());
        var one = filtered["checks"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == "Reachable")!.AsObject();
        Assert.Equal((1, 1), (one["pass"]!.GetValue<int>(), one["fail"]!.GetValue<int>()));
        Assert.All(filtered["checks"]!.AsArray(), c => Assert.True(c!["pass"]!.GetValue<int>() + c["fail"]!.GetValue<int>() + c["unknown"]!.GetValue<int>() + c["not_applicable"]!.GetValue<int>() <= 2));

        // Devices come only as counts: no device id or address in the answer.
        var text = Text(await server.CallToolAsync(key, "oadm_compliance_summary"));
        Assert.DoesNotContain(unreachable.Id.ToString(), text, StringComparison.Ordinal);
        Assert.DoesNotContain("10.26.0.9", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheComplianceChecksListWhatEachChecksWhetherOnOrOffAndHoldNoSettings()
    {
        await using var server = await McpTestServer.StartAsync();
        var compliance = new Proto.ComplianceService.ComplianceServiceClient(server.Host.Invoker);
        await compliance.SaveCheckAsync(new Proto.ComplianceCheckDetail
        {
            Summary = new Proto.ComplianceCheckSummary
            {
                Name = "Old firmware", TypeId = Core.Compliance.BuiltInComplianceTypes.FirmwareId, Enabled = false,
                Devices = new Proto.ComplianceDevices { Tags = { "Lobby" } },
            },
            SettingsJson = """{"minimum":"11.11"}""",
        });
        var key = await server.CreateKeyAsync();

        var checks = await server.ToolAsync(key, "oadm_compliance_checks");
        var firmware = checks["checks"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == "Old firmware")!.AsObject();
        Assert.False(firmware["enabled"]!.GetValue<bool>());
        Assert.False(firmware["built_in"]!.GetValue<bool>());
        Assert.Equal(["Lobby"], firmware["devices"]!["tags"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.False(firmware["devices"]!["all"]!.GetValue<bool>());
        Assert.False(string.IsNullOrWhiteSpace(firmware["kind"]!.GetValue<string>()));
        Assert.Contains(checks["checks"]!.AsArray(), c => c!["built_in"]!.GetValue<bool>());
        Assert.False(firmware.ContainsKey("settings"));

        var onlyOff = await server.ToolAsync(key, "oadm_compliance_checks", new JsonObject { ["enabled"] = false });
        Assert.Equal(["Old firmware"], onlyOff["checks"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()));
        var onlyOn = await server.ToolAsync(key, "oadm_compliance_checks", new JsonObject { ["enabled"] = true });
        Assert.DoesNotContain(onlyOn["checks"]!.AsArray(), c => c!["name"]!.GetValue<string>() == "Old firmware");
    }
}
