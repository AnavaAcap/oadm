using System.Text.Json.Nodes;

using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Server.Mcp;
using Oadm.Server.Tests.Support;

namespace Oadm.Server.Tests.Mcp;

public sealed class McpDelegationTests
{
    [Fact]
    public async Task DelegatedAdministratorCanOnlyUseApprovedToolsAndDevicesAndCannotApprove()
    {
        await using var server = await McpTestServer.StartAsync();
        var one = await DeviceServiceTests.AddDeviceAsync(server.Host, "10.51.0.1", 9701);
        await DeviceServiceTests.AddDeviceAsync(server.Host, "10.51.0.2", 9702);
        var owner = (await server.Host.Get<UserStore>().FindByNameAsync(TestServerHost.AdminUserName, CancellationToken.None))!;
        var views = server.Host.Get<DeviceViewFactory>();
        var native = new AuthenticatedCaller(owner.Id, owner.UserName, owner.RoleId, PermissionSet.All, "local", "laptop", "native-hash")
        {
            Devices = CallerDevices.All,
            CanApprove = true,
        };
        string[] names = ["oadm_devices_search", "oadm_tags_assign_preview", "oadm_tags_assign_run"];
        var grant = new DelegationGrant(1, owner.Id, [new(one.Id, one.Serial)], [HostPermissions.TagsAssign], names);
        var keys = server.Host.Get<AccessKeyStore>();
        var (key, id, _) = await keys.CreateDelegatedAsync(native, "Claude laptop", grant, await views.ForAsync(native, CancellationToken.None), CancellationToken.None);
        var signedIn = (await server.Host.Get<McpAccess>().AuthenticateAsync(key, "local", CancellationToken.None))!;
        Assert.False(signedIn.User.IsAdmin);
        Assert.False(signedIn.User.HasAllDevices);
        Assert.False(signedIn.User.CanApprove);
        Assert.Equal(owner.Id, signedIn.User.Delegation!.OwnerId);
        Assert.False(signedIn.User.Has(Oadm.Sdk.Plugins.PermissionKeys.Task("oadm.acap", "install")));
        Assert.Equal(names.Order(StringComparer.Ordinal), (await server.ToolNamesAsync(key)).Order(StringComparer.Ordinal));
        var found = await server.ToolAsync(key, "oadm_devices_search");
        Assert.Equal(1, found["total"]!.GetValue<int>());
        Assert.Equal(one.Id.ToString(), Assert.Single(found["devices"]!.AsArray())!["id"]!.GetValue<string>());
        var denied = await server.PostAsync(key, McpTestServer.Request("tools/call", new JsonObject { ["name"] = "oadm_logs_audit", ["arguments"] = new JsonObject() }));
        Assert.Contains("does not have that capability", server.LastRaw!, StringComparison.Ordinal);
        Assert.Equal(200, denied.Status);

        await keys.DeleteAsync(id, CancellationToken.None);
        Assert.Equal(401, (await server.PostAsync(key, McpTestServer.Request("tools/list"))).Status);
    }
}
