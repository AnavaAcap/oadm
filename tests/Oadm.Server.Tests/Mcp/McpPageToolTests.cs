using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Core.Auth;
using Oadm.Core.Events;
using Oadm.Core.Plugins;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;
using Oadm.Server.Plugins;
using Oadm.Server.Tests.Support;
using Oadm.Server.Tools;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Mcp;

/// <summary>
/// The read-only tools of the monitoring and maintenance pages (docs/spec/mcp.md "Plugins") in the server: listed while their
/// plugin is on, strict schemas, paged lists with the host's cursor, answers within 32 KB; and the host's recipient tool
/// (target cut to scheme, host and port, never a password). The plugins' own tests check their content and device groups.
/// </summary>
public sealed class McpPageToolTests
{
    /// <summary>Every tool of the page plugins and whether it is a list.</summary>
    private static readonly (string Name, bool Paged)[] PageTools =
    [
        ("oadm_edge_storage_cards", true), ("oadm_recordings_search", true), ("oadm_image_health_results", true), ("oadm_io_ports", false),
        ("oadm_metadata_streams", false), ("oadm_dashboards_list", true), ("oadm_dashboards_values", true), ("oadm_load_test_checks", false),
        ("oadm_load_test_check", true), ("oadm_snapshot_report_sources", false),
    ];

    internal static ICorePlugin[] PagePlugins() =>
    [
        new Oadm.Plugins.EdgeStorage.EdgeStoragePlugin(),
        new Oadm.Plugins.EdgeRecordingExport.EdgeRecordingExportPlugin(),
        new Oadm.Plugins.ImageHealth.ImageHealthPlugin(),
        new Oadm.Plugins.IoMonitor.IoMonitorPlugin(),
        new Oadm.Plugins.MetadataMonitor.MetadataMonitorPlugin(),
        new Oadm.Plugins.Dashboards.DashboardsPlugin(),
        new Oadm.Plugins.LoadTest.LoadTestPlugin(),
        new Oadm.Plugins.SnapshotReport.SnapshotReportPlugin(),
    ];

    private static async Task<McpTestServer> StartAsync(params IActionPlugin[] actions)
    {
        var server = await McpTestServer.StartAsync();
        var registry = server.Host.Get<PluginRegistry>();
        foreach (var plugin in PagePlugins())
        {
            registry.RegisterCorePlugin(plugin, new PluginOrigin(plugin.Id, "1.0.0", null) { DisplayName = plugin.DisplayName, MinSdkVersion = SdkInfo.Version });
        }

        foreach (var action in actions)
        {
            registry.RegisterActionPlugin(action, new PluginOrigin("test.hook", "1.0.0", null) { DisplayName = action.DisplayName });
        }

        await server.Host.Get<PluginActivation>().LoadAsync(CancellationToken.None);
        await server.Host.Get<RoleStore>().SyncCatalogAsync(PermissionCatalog.Build(registry), CancellationToken.None);
        await server.Host.Get<CorePluginHost>().StartAllAsync(CancellationToken.None);
        return server;
    }

    [Fact]
    public async Task ThePagesOfferTheirReadsWithStrictSchemasAndAnswerWithin32KB()
    {
        await using var server = await StartAsync();
        var key = await server.CreateKeyAsync();
        var reply = await server.PostAsync(key, McpTestServer.Request("tools/list"));
        var listed = reply.Body!["result"]!["tools"]!.AsArray().Select(t => t!.AsObject()).ToDictionary(t => t["name"]!.GetValue<string>());
        Assert.All(PageTools, t => Assert.False(t.Name.EndsWith("_run", StringComparison.Ordinal) || t.Name.EndsWith("_preview", StringComparison.Ordinal), t.Name)); // read as a change
        foreach (var (name, paged) in PageTools)
        {
            Assert.True(listed.TryGetValue(name, out var tool), name);
            Assert.False(tool!["inputSchema"]!["additionalProperties"]!.GetValue<bool>(), name);
            Assert.True(tool["annotations"]!["readOnlyHint"]!.GetValue<bool>(), name);
            var properties = tool["inputSchema"]!["properties"]!.AsObject();
            Assert.Equal(paged, properties.ContainsKey("cursor") && properties.ContainsKey("limit"));
            Assert.False(properties.ContainsKey("after"), name);
        }

        Assert.DoesNotContain(listed.Keys, n => n.EndsWith("_preview", StringComparison.Ordinal) && PageTools.Any(t => n.StartsWith(t.Name, StringComparison.Ordinal)));

        // The reads that need nothing answer an empty page or state within the limit; the ones that need an id say so.
        foreach (var name in new[] { "oadm_edge_storage_cards", "oadm_image_health_results", "oadm_metadata_streams", "oadm_dashboards_list", "oadm_load_test_checks" })
        {
            var answer = await server.ToolAsync(key, name);
            Assert.True(ToolAnswers.SizeOf(answer) <= ToolAnswers.MaxBytes, name);
        }

        Assert.Null((await server.ToolAsync(key, "oadm_edge_storage_cards"))["next_cursor"]);
        foreach (var (name, field) in new[] { ("oadm_io_ports", "device_id"), ("oadm_recordings_search", "device_id"), ("oadm_dashboards_values", "dashboard_id"), ("oadm_load_test_check", "run_id"), ("oadm_snapshot_report_sources", "device_ids") })
        {
            var refused = await server.CallToolAsync(key, name);
            Assert.True(refused["isError"]!.GetValue<bool>(), name);
            Assert.Contains($"\"{field}\"", refused["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task APageToolGoesWhileItsPluginIsOff()
    {
        await using var server = await StartAsync();
        var key = await server.CreateKeyAsync();
        Assert.Contains("oadm_edge_storage_cards", await server.ToolNamesAsync(key));

        await server.Host.Plugins.SetPackageEnabledAsync(new Proto.SetPackageEnabledRequest { Id = "oadm.edge-storage", Enabled = false });
        var names = await server.ToolNamesAsync(key);
        Assert.DoesNotContain("oadm_edge_storage_cards", names);
        Assert.Contains("oadm_image_health_results", names); // the others stay
    }

    [Fact]
    public async Task TheRecipientsShowTheirTargetAndRulesButNeverAPasswordOrAPath()
    {
        await using var server = await StartAsync(new HookAction());
        var key = await server.CreateKeyAsync();
        var events = new Proto.EventService.EventServiceClient(server.Host.Invoker);
        var saved = await events.SaveRecipientAsync(new Proto.SaveRecipientRequest
        {
            Name = "Teams",
            TypeId = HookAction.TypeId,
            SettingsJson = new JsonObject { ["url"] = "https://hooks.example.com:8443/very/secret/path?sig=abc", ["token"] = "t0ken-secret" }.ToJsonString(),
        });
        var rule = new RuleDefinition
        {
            Name = "Gate alarm",
            Conditions = [new RuleCondition { Kind = RuleConditionKind.Event, EventTypeId = BuiltInEvents.Unreachable, Devices = new DeviceSelector { Mode = DeviceSelectorMode.All } }],
            Actions = [new RuleAction { ActionId = HookAction.ActionId, Settings = new JsonObject { ["recipient"] = saved.Id } }],
        };
        await events.SaveRuleAsync(new Proto.SaveRuleRequest { DefinitionJson = rule.ToJson() });

        var answer = await server.ToolAsync(key, "oadm_recipients_list");
        Assert.Equal(1, answer["total"]!.GetValue<int>());
        var recipient = answer["recipients"]![0]!;
        Assert.Equal(("Teams", "Hook", "https://hooks.example.com:8443"), (recipient["name"]!.GetValue<string>(), recipient["type"]!.GetValue<string>(), recipient["target"]!.GetValue<string>()));
        Assert.True(recipient["has_password"]!.GetValue<bool>());
        Assert.Equal(1, recipient["rules"]!.GetValue<int>());
        Assert.Equal("Gate alarm", recipient["used_by"]![0]!.GetValue<string>());
        Assert.Null(recipient["connection"]); // a type without a shared connection
        Assert.Equal(1, answer["types"]![0]!["count"]!.GetValue<int>());
        var text = answer.ToJsonString();
        Assert.DoesNotContain("t0ken-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret/path", text, StringComparison.Ordinal);
        Assert.DoesNotContain("sig=abc", text, StringComparison.Ordinal);
        Assert.Equal(0, (await server.ToolAsync(key, "oadm_recipients_list", new JsonObject { ["type"] = "oadm.mqtt" }))["total"]!.GetValue<int>());
    }

    /// <summary>An action with a recipient type that holds a URL and a token (like Send HTTP request).</summary>
    private sealed class HookAction : IActionPlugin
    {
        public const string TypeId = "test.hook";

        public const string ActionId = "test.hook-action";

        public string Id => ActionId;

        public string DisplayName => "Call hook";

        public string? IconKey => null;

        public bool NeedsDevice => false;

        public IReadOnlyList<RecipientType> RecipientTypes { get; } =
        [
            new(TypeId, "Hook", "send", [ActionField.Text("url", "URL", required: true), ActionField.Password("token", "Token")], ActionId) { Target = "{url}" },
        ];

        public IReadOnlyList<ActionField> Fields { get; } = [ActionField.Recipient("recipient", "Recipient", [TypeId])];

        public IReadOnlyDictionary<string, string> Validate(JsonElement settings, bool statefulRule) => new Dictionary<string, string>();

        public Task ExecuteAsync(IActionContext ctx, ActionInvocation invocation, CancellationToken ct) => Task.CompletedTask;
    }
}
