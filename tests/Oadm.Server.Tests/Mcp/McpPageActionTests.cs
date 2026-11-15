using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;

using Oadm.Core.Auth;
using Oadm.Core.Plugins;
using Oadm.Sdk.Plugins;
using Oadm.Server.Plugins;
using Oadm.Server.Tests.Support;
using Oadm.Server.Tools;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Mcp;

/// <summary>
/// Page methods as tools (docs/spec/mcp.md "Plugins", SDK <c>PluginPageTools</c>, decision 2026-11-15): a change is a preview with
/// the page's validation that writes nothing and a run that validates again and calls the page's code with the caller, once; the
/// tool has the rights of its page method (role, permission, device group, audit); a read runs at once; the plugin off: no tools.
/// </summary>
public sealed class McpPageActionTests
{
    private static async Task<(McpTestServer Server, GatesPlugin Plugin)> StartAsync()
    {
        var server = await McpTestServer.StartAsync();
        var registry = server.Host.Get<PluginRegistry>();
        var plugin = new GatesPlugin();
        registry.RegisterCorePlugin(plugin, new PluginOrigin(plugin.Id, "1.0.0", null) { DisplayName = plugin.DisplayName, MinSdkVersion = SdkInfo.Version });
        await server.Host.Get<PluginActivation>().LoadAsync(CancellationToken.None);
        await server.Host.Get<RoleStore>().SyncCatalogAsync(PermissionCatalog.Build(registry), CancellationToken.None);
        await server.Host.Get<CorePluginHost>().StartAllAsync(CancellationToken.None);
        return (server, plugin);
    }

    private static string ErrorText(JsonObject result) => result["content"]![0]!["text"]!.GetValue<string>();

    [Fact]
    public void TheHelperDeclaresTheToolsWithTheirTypesAndMapsEveryToolMethodToItsPageMethod()
    {
        var set = GatesPlugin.Set;
        Assert.Equal(["gates", "gate_save", "gate_delete"], set.Tools.Select(t => t.Name));
        var save = set.ToolOf("tool.gate_save.preview")!;
        Assert.True(save.Changes);
        Assert.Equal(typeof(GatesPlugin.SaveArgs), save.InputType);
        Assert.Equal(typeof(GatesPlugin.SavePlan), save.OutputType);
        Assert.Equal(typeof(GatesPlugin.Saved), save.RunOutputType);
        Assert.Equal(GatesPlugin.SaveMethod, set.PageMethodOf("tool.gate_save"));
        Assert.Equal(GatesPlugin.SaveMethod, set.PageMethodOf("tool.gate_save.preview"));
        Assert.Equal(GatesPlugin.ListMethod, set.RulesMethodOf("tool.gates"));
        Assert.Equal("other", set.RulesMethodOf("other"));
        Assert.False(set.Handles("tool.gates.preview")); // a read has no preview
        Assert.False(set.Handles(GatesPlugin.SaveMethod)); // the page method itself is no tool
        Assert.Throws<ArgumentException>(() => new PluginPageTools<GatesPlugin>().Read<GatesPlugin.NoArgs, GatesPlugin.Saved>(new("x", "X", "X."), "tool.y", (_, _, _, _) => Task.FromResult(new GatesPlugin.Saved("a"))));
    }

    [Fact]
    public async Task AChangeIsAPreviewWithThePagesChecksThatWritesNothingAndARunThatCallsThePageOnce()
    {
        var (server, plugin) = await StartAsync();
        await using var _ = server;
        var key = await server.CreateKeyAsync();
        var names = await server.ToolNamesAsync(key);
        Assert.Contains("oadm_gates_gates", names);
        Assert.Contains("oadm_gates_gate_save_preview", names);
        Assert.Contains("oadm_gates_gate_delete_run", names);
        Assert.DoesNotContain("oadm_gates_gates_preview", names);

        var preview = await server.ToolAsync(key, "oadm_gates_gate_save_preview", new JsonObject { ["name"] = "North" });
        Assert.Equal(1, preview["will_change"]!.GetValue<int>());
        Assert.Equal("gate", preview["unit"]!.GetValue<string>());
        Assert.Empty(plugin.Gates); // the preview wrote nothing

        var run = await server.ToolAsync(key, "oadm_gates_gate_save_run", new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() });
        Assert.Equal("North", run["name"]!.GetValue<string>());
        Assert.Equal(["North"], plugin.Gates);
        Assert.Equal(Oadm.Core.Auth.AccessKeyStore.AgentNameOf("agent"), plugin.LastCaller); // the page's code with the caller
        var again = await server.CallToolAsync(key, "oadm_gates_gate_save_run", new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() });
        Assert.Contains(PluginTools.PreviewEnded, ErrorText(again), StringComparison.Ordinal);
        Assert.Single(plugin.Gates);

        // The run is the page method's audited call; the preview leaves no entry.
        var audit = (await server.Host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries.Where(e => e.Action == AuditActions.PluginCall).ToList();
        Assert.Single(audit);

        // A read runs at once, with the page method's answer.
        Assert.Equal(["North"], (await server.ToolAsync(key, "oadm_gates_gates"))["names"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task InvalidInputIsRefusedWithThePagesTextsAndTheRunValidatesAgain()
    {
        var (server, plugin) = await StartAsync();
        await using var _ = server;
        var key = await server.CreateKeyAsync();

        var unknown = await server.CallToolAsync(key, "oadm_gates_gate_save_preview", new JsonObject { ["name"] = "A", ["colour"] = "red" });
        Assert.True(unknown["isError"]!.GetValue<bool>()); // the strict schema
        var empty = await server.CallToolAsync(key, "oadm_gates_gate_save_preview", new JsonObject { ["name"] = " " });
        var field = Assert.Single(JsonNode.Parse(ErrorText(empty))!["errors"]!.AsArray())!;
        Assert.Equal("name", field["field"]!.GetValue<string>());
        Assert.Equal(GatesPlugin.EnterName, field["error"]!.GetValue<string>());

        // Taken between the preview and the run: the run's check refuses, nothing more is written.
        var first = await server.ToolAsync(key, "oadm_gates_gate_save_preview", new JsonObject { ["name"] = "Dock" });
        var second = await server.ToolAsync(key, "oadm_gates_gate_save_preview", new JsonObject { ["name"] = "Dock" });
        await server.ToolAsync(key, "oadm_gates_gate_save_run", new JsonObject { ["handle"] = first["handle"]!.GetValue<string>() });
        var refused = await server.CallToolAsync(key, "oadm_gates_gate_save_run", new JsonObject { ["handle"] = second["handle"]!.GetValue<string>() });
        Assert.Contains(GatesPlugin.NameTaken, ErrorText(refused), StringComparison.Ordinal);
        Assert.Equal(["Dock"], plugin.Gates);

        // A refusal without a field: the page's text.
        var missing = await server.CallToolAsync(key, "oadm_gates_gate_delete_preview", new JsonObject { ["name"] = "Nowhere" });
        Assert.Contains(GatesPlugin.NotFound, ErrorText(missing), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheToolsHaveTheRightsOfThePageMethodsForRolesAndDeviceGroups()
    {
        var (server, _) = await StartAsync();
        await using var __ = server;
        var roles = new Proto.RoleService.RoleServiceClient(server.Host.Invoker);
        var saved = await roles.SaveRolesAsync(new Proto.SaveRolesRequest { Roles = { new Proto.RoleChange { Name = "Viewers", Permissions = { PermissionKeys.Of(GatesPlugin.PluginId, PermissionKeys.Show) } } } });
        var role = saved.Roles.Single(r => r.Name == "Viewers");
        var viewer = await server.CreateKeyAsync(await server.Host.InvokerForRoleAsync("viewer1", Guid.Parse(role.Id)));
        var shown = await server.ToolNamesAsync(viewer);
        Assert.Contains("oadm_gates_gates", shown);
        Assert.DoesNotContain("oadm_gates_gate_save_preview", shown); // the page's Save needs "manage"
        Assert.DoesNotContain("oadm_gates_gate_save_run", shown);

        // Delete is for users with all devices on the page: a group's key gets neither half.
        var group = await GroupCallers.ForTagAsync(server.Host, "tech", "Lobby");
        var grouped = await server.CreateKeyAsync(group.Invoker);
        var names = await server.ToolNamesAsync(grouped);
        Assert.Contains("oadm_gates_gate_save_preview", names);
        Assert.DoesNotContain("oadm_gates_gate_delete_preview", names);
        Assert.True((await server.CallToolAsync(grouped, "oadm_gates_gate_delete_preview", new JsonObject { ["name"] = "Dock" }))["isError"]!.GetValue<bool>());

        // Turned off: no tool of the plugin.
        await server.Host.Plugins.SetPackageEnabledAsync(new Proto.SetPackageEnabledRequest { Id = GatesPlugin.PluginId, Enabled = false });
        Assert.DoesNotContain(await server.ToolNamesAsync(await server.CreateKeyAsync(name: "other")), n => n.StartsWith("oadm_gates_", StringComparison.Ordinal));
    }

    /// <summary>A plugin whose page saves, deletes and lists gates; its tools are the page methods wrapped by the SDK helper.</summary>
    private sealed class GatesPlugin : ICorePlugin
    {
        public const string PluginId = "tests.gates";
        public const string ListMethod = "list";
        public const string SaveMethod = "save";
        public const string DeleteMethod = "delete";
        public const string EnterName = "Enter a name.";
        public const string NameTaken = "A gate with this name exists.";
        public const string NotFound = "This gate no longer exists.";

        private readonly List<string> _gates = [];

        public static PluginPageTools<GatesPlugin> Set { get; } = new PluginPageTools<GatesPlugin>()
            .Read<NoArgs, ListAnswer>(new("gates", "Gates", "Lists the gates."), ListMethod, (plugin, _, _, _) => Task.FromResult(new ListAnswer([.. plugin._gates])))
            .Change<SaveArgs, SavePlan, Saved>(
                new("gate_save", "Save gate", "Adds a gate."),
                SaveMethod,
                (plugin, _, input, _) => Task.FromResult(plugin.CheckSave(input.Args.Name)),
                (plugin, caller, _, plan, _) =>
                {
                    plugin.LastCaller = caller.UserName;
                    plugin._gates.Add(plan.Name);
                    return Task.FromResult(new Saved(plan.Name));
                })
            .Change<SaveArgs, SavePlan, Saved>(
                new("gate_delete", "Delete gate", "Deletes a gate.") { Destructive = true },
                DeleteMethod,
                (plugin, _, input, _) => Task.FromResult(plugin._gates.Contains(input.Args.Name.Trim(), StringComparer.Ordinal) ? new SavePlan(input.Args.Name.Trim()) : throw new InvalidOperationException(NotFound)),
                (plugin, _, _, plan, _) =>
                {
                    plugin._gates.Remove(plan.Name);
                    return Task.FromResult(new Saved(plan.Name));
                });

        public IReadOnlyList<string> Gates => _gates;

        public string? LastCaller { get; private set; }

        public string Id => PluginId;

        public string DisplayName => "Gates";

        public string? IconKey => null;

        public string? ToolArea => "gates";

        public IReadOnlyList<ITaskPlugin> TaskPlugins { get; } = [];

        public IReadOnlyList<PermissionDefinition> Permissions { get; } = [new(PermissionKeys.Manage, "Manage gates", "Add and delete gates.") { OperatorDefault = false }];

        public IReadOnlyList<PluginTool> Tools => Set.Tools;

        public Task StartAsync(ICorePluginContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => throw new InvalidOperationException("old overload");

        public string? RequiredPermission(string method) => Set.RulesMethodOf(method) is SaveMethod or DeleteMethod ? PermissionKeys.Manage : PermissionKeys.Show;

        public UserRole RequiredRole(string method) => UserRole.Operator;

        public bool IsAudited(string method) => !PluginTool.IsPreviewMethod(method) && Set.RulesMethodOf(method) is SaveMethod or DeleteMethod;

        public bool RequiresAllDevices(string method) => Set.RulesMethodOf(method) == DeleteMethod;

        public async Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct) =>
            Set.Handles(method) ? await Set.InvokeAsync(this, caller, method, payloadJson, ct) : throw new ArgumentException("Unknown method.", nameof(method));

        private SavePlan CheckSave(string name)
        {
            var trimmed = name.Trim();
            if (trimmed.Length == 0)
            {
                throw new PluginToolInputException("name", EnterName);
            }

            return _gates.Contains(trimmed, StringComparer.Ordinal) ? throw new PluginToolInputException("name", NameTaken) : new SavePlan(trimmed);
        }

        internal sealed record NoArgs;

        internal sealed record SaveArgs([property: Description("The gate's name."), MaxLength(40)] string Name);

        internal sealed record SavePlan(string Name) : PluginToolPlan(1, "gate");

        internal sealed record Saved(string Name);

        internal sealed record ListAnswer(IReadOnlyList<string> Names);
    }
}
