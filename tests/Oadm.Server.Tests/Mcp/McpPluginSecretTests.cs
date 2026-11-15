using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
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
/// The secrets of a plugin tool's change (docs/spec/mcp.md "Secrets", SDK <c>PluginToolPlan.SecretsNeeded</c>): the plan names
/// them, the host takes that out of the answer and declares the slots, checks an entered value with the preview method, hands
/// the values to the run once in <c>secrets</c>, and never lets a caller send <c>secrets</c> itself.
/// </summary>
public sealed class McpPluginSecretTests
{
    private const string Canary = "canary-plugin-pin-4831";

    private static async Task<(McpTestServer Server, LocksPlugin Plugin)> StartAsync()
    {
        var server = await McpTestServer.StartAsync();
        var registry = server.Host.Get<PluginRegistry>();
        var plugin = new LocksPlugin();
        registry.RegisterCorePlugin(plugin, new PluginOrigin(plugin.Id, "1.0.0", null) { DisplayName = plugin.DisplayName, MinSdkVersion = SdkInfo.Version });
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

    [Fact]
    public async Task APluginsPlanNamesItsSecretsTheHostAsksThePersonAndTheRunGetsThemOnce()
    {
        var (server, plugin) = await StartAsync();
        await using var _ = server;
        var key = await server.CreateKeyAsync(name: "Lobby-bot");
        var answers = new StringBuilder();

        var tools = await server.PostAsync(key, McpTestServer.Request("tools/list"));
        var schema = tools.Body!["result"]!["tools"]!.AsArray().Single(t => t!["name"]!.GetValue<string>() == "oadm_locks_lock_set_preview")!["outputSchema"]!["properties"]!.AsObject();
        Assert.DoesNotContain(PluginToolSecrets.Property, schema.Select(p => p.Key));
        Assert.Contains("needs_secrets", schema.Select(p => p.Key));

        // A caller never sends the values itself: the strict schema refuses them.
        var sent = await server.CallToolAsync(key, "oadm_locks_lock_set_preview", new JsonObject { ["name"] = "North", ["secrets"] = new JsonObject { ["pin"] = Canary } });
        Assert.True(sent["isError"]!.GetValue<bool>());

        var preview = await server.ToolAsync(key, "oadm_locks_lock_set_preview", new JsonObject { ["name"] = "North" });
        answers.Append(preview.ToJsonString());
        Assert.False(preview.ContainsKey(PluginToolSecrets.Property));
        Assert.Equal("pin", Assert.Single(preview["needs_secrets"]!.AsArray())!.GetValue<string>());
        Assert.Contains("PIN code: ", preview["secret_links"]!["pin"]!["command"]!.GetValue<string>(), StringComparison.Ordinal);
        var url = preview["secret_links"]!["pin"]!["url"]!.GetValue<string>();
        var handle = preview["handle"]!.GetValue<string>();
        Assert.Empty(plugin.Pins); // the preview wrote nothing

        var early = await server.CallToolAsync(key, "oadm_locks_lock_set_run", new JsonObject { ["handle"] = handle });
        Assert.Contains("Enter the PIN code.", early.ToJsonString(), StringComparison.Ordinal);

        using (var shortPin = await PutAsync(server, key, url, "12"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, shortPin.StatusCode); // the page's own rule, through the preview method
            Assert.Contains(LocksPlugin.TooShort, await shortPin.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using (var set = await PutAsync(server, key, url, Canary))
        {
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
            answers.Append(await set.Content.ReadAsStringAsync());
        }

        var run = await server.ToolAsync(key, "oadm_locks_lock_set_run", new JsonObject { ["handle"] = handle });
        answers.Append(run.ToJsonString());
        Assert.True(run["pin_set"]!.GetValue<bool>());
        Assert.Equal(Canary, plugin.Pins["North"]);
        var again = await server.CallToolAsync(key, "oadm_locks_lock_set_run", new JsonObject { ["handle"] = handle });
        Assert.True(again["isError"]!.GetValue<bool>());

        var audit = (await server.Host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        Assert.Contains(audit, e => e.Action == AuditActions.SecretSet && e.Target == "PIN code" && e.UserName == AccessKeyStore.AgentNameOf("Lobby-bot"));
        await SecretScan.AssertNowhereAsync(server.Host, Canary, answers.ToString());
    }

    /// <summary>A page with locks whose PIN the person sets (at least 4 characters, the page's rule).</summary>
    private sealed class LocksPlugin : ICorePlugin
    {
        public const string TooShort = "Use at least 4 digits.";

        public static PluginPageTools<LocksPlugin> Set { get; } = new PluginPageTools<LocksPlugin>()
            .Change<LockArgs, LockPlan, LockSet>(
                new("lock_set", "Set lock PIN", "Sets the PIN of a lock."),
                "setPin",
                (_, _, input, _) => Task.FromResult(input.Secret("pin") is { Length: < 4 }
                    ? throw new PluginToolInputException("secrets.pin", TooShort)
                    : new LockPlan(input.Args.Name.Trim()) { SecretsNeeded = [new PluginToolSecret("pin", "PIN code")] }),
                (plugin, _, input, plan, _) =>
                {
                    plugin.Pins[plan.Name] = input.Secret("pin") ?? throw new InvalidOperationException("Enter the PIN code.");
                    return Task.FromResult(new LockSet(plan.Name, true));
                });

        public Dictionary<string, string> Pins { get; } = new(StringComparer.Ordinal);

        public string Id => "tests.locks";

        public string DisplayName => "Locks";

        public string? IconKey => null;

        public string? ToolArea => "locks";

        public IReadOnlyList<ITaskPlugin> TaskPlugins { get; } = [];

        public IReadOnlyList<PermissionDefinition> Permissions { get; } = [new(PermissionKeys.Manage, "Manage locks", "Set PIN codes.") { OperatorDefault = false }];

        public IReadOnlyList<PluginTool> Tools => Set.Tools;

        public Task StartAsync(ICorePluginContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => throw new InvalidOperationException("old overload");

        public string? RequiredPermission(string method) => PermissionKeys.Manage;

        public UserRole RequiredRole(string method) => UserRole.Operator;

        public bool IsAudited(string method) => !PluginTool.IsPreviewMethod(method);

        public async Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct) =>
            Set.Handles(method) ? await Set.InvokeAsync(this, caller, method, payloadJson, ct) : throw new ArgumentException("Unknown method.", nameof(method));

        internal sealed record LockArgs([property: Description("The lock."), MaxLength(40)] string Name);

        internal sealed record LockPlan(string Name) : PluginToolPlan(1, "lock");

        internal sealed record LockSet(string Name, bool PinSet);
    }
}
