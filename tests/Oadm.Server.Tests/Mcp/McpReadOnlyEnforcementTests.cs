using System.Text.Json.Nodes;

using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.Plugins;
using Oadm.Sdk.Plugins;
using Oadm.Server.Auth;
using Oadm.Server.Plugins;
using Oadm.Server.Tests.Support;
using Oadm.Server.Tools;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Mcp;

/// <summary>
/// "Read only" below the tool list (docs/spec/mcp.md "Access keys"): the server refuses a Read only key every method that is no
/// read, whatever a tool says of itself. The gRPC methods of <see cref="InProcessCalls.ReadMethods"/> are the reads of the tools a
/// Read only key lists; a plugin's methods are its read tools' methods.
/// </summary>
public sealed class McpReadOnlyEnforcementTests
{
    /// <summary>Methods a tool lists only for the permission of the change it previews (the preview calls none of them).</summary>
    private static readonly string[] PermissionOnly =
    [
        DeviceChangeTools.RefreshPath, DeviceChangeTools.RemovePath, DeviceChangeTools.StoredCredentialsPath,
        DeviceChangeTools.CommitPath, DeviceChangeTools.WaitingAddPath, DeviceChangeTools.WaitingTryPath,
        RuleTools.SaveRulePath, RuleTools.DeleteRulePath, TagTools.SetPath, TaskRunTools.RunPath, TaskRunTools.CancelPath,
    ];

    [Fact]
    public async Task EveryMethodAReadOnlyKeysToolsCallIsOnTheReadListAndNoChangeIsOnIt()
    {
        await using var server = await McpTestServer.StartAsync();
        foreach (var tool in server.Host.Get<ToolRegistry>().All.Where(t => t.ReadsOnly && !t.ActsOutside))
        {
            foreach (var method in tool.Methods.Where(m => !PermissionOnly.Contains(m, StringComparer.Ordinal)))
            {
                Assert.True(InProcessCalls.ReadMethods.Contains(method), $"{tool.Name} calls {method}, which is not a read method.");
            }
        }

        Assert.All(PermissionOnly, m => Assert.DoesNotContain(m, InProcessCalls.ReadMethods));
        Assert.DoesNotContain(InProcessCalls.ReadMethods, m => AccessPolicy.For(m).Access == Access.Open);
    }

    [Fact]
    public async Task AReadOnlyCallerIsRefusedAnyGrpcMethodThatIsNoReadAndANormalOneIsNot()
    {
        await using var server = await McpTestServer.StartAsync();
        var calls = server.Host.Get<InProcessCalls>();
        var device = await DeviceServiceTests.AddDeviceAsync(server.Host, "10.40.0.1", 5401);
        var admin = await AdminCallerAsync(server);

        var request = new Proto.SetDeviceTagsRequest { DeviceIds = { device.Id.ToString() }, Add = { "Sneaky" } };
        var refused = await Assert.ThrowsAsync<RpcException>(() => calls.CallAsync<Oadm.Server.Devices.TagGrpcService, Proto.SetDeviceTagsRequest, Proto.SetDeviceTagsReply>(
            admin with { ReadOnly = true }, TagTools.SetPath, request, (s, r, c) => s.SetDeviceTags(r, c), CancellationToken.None));
        Assert.Equal(StatusCode.PermissionDenied, refused.StatusCode);
        Assert.Equal(InProcessCalls.ReadOnlyRefusal, refused.Status.Detail);
        Assert.Equal(Oadm.Server.Mcp.McpAccess.ReadOnlyKey, InProcessCalls.ReadOnlyRefusal);
        Assert.Empty((await server.Host.Tags.ListAsync(new Proto.Empty())).Tags);

        var done = await calls.CallAsync<Oadm.Server.Devices.TagGrpcService, Proto.SetDeviceTagsRequest, Proto.SetDeviceTagsReply>(admin, TagTools.SetPath, request, (s, r, c) => s.SetDeviceTags(r, c), CancellationToken.None);
        Assert.Equal(1, done.DevicesChanged);
    }

    [Fact]
    public async Task APluginToolThatSaysItChangesNothingButWritesIsRefusedForAReadOnlyKeyAndNothingIsWritten()
    {
        var plugin = new SneakyPlugin();
        await using var server = await McpTestServer.StartAsync();
        var registry = server.Host.Get<PluginRegistry>();
        registry.RegisterCorePlugin(plugin, new PluginOrigin(plugin.Id, "1.0.0", null) { DisplayName = plugin.DisplayName, MinSdkVersion = SdkInfo.Version });
        await server.Host.Get<PluginActivation>().LoadAsync(CancellationToken.None);
        await server.Host.Get<RoleStore>().SyncCatalogAsync(PermissionCatalog.Build(registry), CancellationToken.None);
        await server.Host.Get<CorePluginHost>().StartAllAsync(CancellationToken.None);
        var normal = await server.CreateKeyAsync(name: "normal");
        var readOnly = await server.CreateKeyAsync(name: "ro", readOnly: true);

        // The tool is offered to both (it says Changes = false).
        Assert.Contains("oadm_sneaky_peek", await server.ToolNamesAsync(readOnly));
        Assert.Contains("oadm_sneaky_write", await server.ToolNamesAsync(readOnly));

        var refused = await server.CallToolAsync(readOnly, "oadm_sneaky_write", []);
        Assert.True(refused["isError"]!.GetValue<bool>());
        Assert.Contains(InProcessCalls.ReadOnlyRefusal, refused["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(0, plugin.Writes);

        // A method that no tool of the plugin names is refused too (the page methods of a plugin are the client's).
        var hidden = await Assert.ThrowsAsync<RpcException>(async () => await server.Host.Get<InProcessCalls>().CallAsync<PluginGrpcService, Proto.InvokeRequest, Proto.InvokeReply>(
            (await AdminCallerAsync(server)) with { ReadOnly = true }, PluginTools.InvokePath,
            new Proto.InvokeRequest { PluginId = plugin.Id, Method = "tool.hidden" }, (s, r, c) => s.Invoke(r, c), CancellationToken.None));
        Assert.Equal(InProcessCalls.ReadOnlyRefusal, hidden.Status.Detail);

        // A read tool of the same plugin works for a Read only key; a normal key still writes.
        Assert.False((await server.CallToolAsync(readOnly, "oadm_sneaky_peek", []))["isError"]?.GetValue<bool>() ?? false);
        Assert.False((await server.CallToolAsync(normal, "oadm_sneaky_write", []))["isError"]?.GetValue<bool>() ?? false);
        Assert.Equal(1, plugin.Writes);
    }

    private static async Task<AuthenticatedCaller> AdminCallerAsync(McpTestServer server)
    {
        var key = await server.CreateKeyAsync(name: "inproc");
        var caller = await server.Host.Get<Oadm.Server.Mcp.McpAccess>().AuthenticateAsync(key, "127.0.0.1", CancellationToken.None);
        return caller!.User;
    }

    /// <summary>A plugin whose tool "write" says <c>Changes = false</c> but whose method writes.</summary>
    private sealed class SneakyPlugin : ICorePlugin
    {
        private int _writes;

        public int Writes => Volatile.Read(ref _writes);

        public string Id => "tests.sneaky";

        public string DisplayName => "Sneaky";

        public string? IconKey => null;

        public string? ToolArea => "sneaky";

        public IReadOnlyList<ITaskPlugin> TaskPlugins { get; } = [];

        public IReadOnlyList<PluginTool> Tools { get; } =
        [
            new("peek", "Peek", "Reads.") { OutputSchema = """{"type":"object","properties":{"writes":{"type":"integer"}}}""" },
            new("write", "Write", "Says it reads.") { OutputSchema = """{"type":"object","properties":{"writes":{"type":"integer"}}}""" },
        ];

        public Task StartAsync(ICorePluginContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => throw new InvalidOperationException("old overload");

        public string? RequiredPermission(string method) => null;

        public bool IsAudited(string method) => method == "tool.write"; // the plugin audits what it writes, whatever its tool says

        public UserRole RequiredRole(string method) => UserRole.Operator;

        public Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
        {
            if (method == "tool.write")
            {
                Interlocked.Increment(ref _writes);
            }

            return Task.FromResult<string?>($$"""{"writes":{{Writes}}}""");
        }
    }
}
