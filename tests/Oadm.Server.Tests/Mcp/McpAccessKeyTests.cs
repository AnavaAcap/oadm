using System.Text.Json.Nodes;

using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Server.Auth;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Mcp;

/// <summary>
/// Access keys of the MCP server (docs/spec/mcp.md "Access keys"): Administrator only, each an identity of its own with a name,
/// a role, devices and an end; its calls run with the key's role and devices of now; its role and device group cannot be
/// deleted while it names them; Delete ends it at once.
/// </summary>
public sealed class McpAccessKeyTests
{
    private static Proto.AccessKeyService.AccessKeyServiceClient Keys(CallInvoker invoker) => new(invoker);

    private static string? FieldOf(RpcException ex) => ex.Trailers.GetValue(AccessKeyGrpcService.FieldTrailer);

    private static async Task<Proto.RoleInfo> RoleAsync(McpTestServer server, string name, params string[] permissions)
    {
        var saved = await new Proto.RoleService.RoleServiceClient(server.Host.Invoker).SaveRolesAsync(new Proto.SaveRolesRequest
        {
            Roles = { new Proto.RoleChange { Name = name, Permissions = { permissions } } },
        });
        return saved.Roles.Single(r => r.Name == name);
    }

    private static async Task<(Guid Group, string TagId)> LobbyGroupAsync(McpTestServer server, int devices, int others)
    {
        var tag = await server.Host.Tags.CreateAsync(new Proto.CreateTagRequest { Name = "Lobby" });
        var ids = new List<string>();
        for (var i = 0; i < devices + others; i++)
        {
            var device = await DeviceServiceTests.AddDeviceAsync(server.Host, $"10.41.0.{i + 1}", 9100 + i);
            if (i < devices)
            {
                ids.Add(device.Id.ToString());
            }
        }

        await server.Host.Tags.SetDeviceTagsAsync(new Proto.SetDeviceTagsRequest { DeviceIds = { ids }, Add = { "Lobby" } });
        var group = await GroupCallers.GroupAsync(server.Host, "Lobby group", new Proto.DeviceGroupRules { TagIds = { tag.Id } });
        return (group, tag.Id);
    }

    [Fact]
    public async Task OnlyAdministratorsListCreateAndDeleteKeys()
    {
        await using var server = await McpTestServer.StartAsync();
        var operatorCalls = Keys(await server.Host.InvokerForUserAsync("op", Oadm.Sdk.Plugins.UserRole.Operator));
        var list = await Assert.ThrowsAsync<RpcException>(async () => await operatorCalls.ListAsync(new Proto.Empty()));
        var create = await Assert.ThrowsAsync<RpcException>(async () => await operatorCalls.CreateAsync(new Proto.CreateAccessKeyRequest
        {
            Name = "x", Days = 90, RoleId = BuiltInRoles.ViewerId.ToString(), AllDevices = true,
        }));
        var delete = await Assert.ThrowsAsync<RpcException>(async () => await operatorCalls.DeleteAsync(new Proto.AccessKeyIdRequest { Id = Guid.NewGuid().ToString() }));
        Assert.All(new[] { list, create, delete }, e => Assert.Equal(StatusCode.PermissionDenied, e.StatusCode));
        Assert.Empty((await Keys(server.Host.Invoker).ListAsync(new Proto.Empty())).Keys);
    }

    [Fact]
    public async Task CreateRefusesEachFieldBelowItsFieldAndANameTakenInAnyCase()
    {
        await using var server = await McpTestServer.StartAsync();
        var keys = Keys(server.Host.Invoker);
        var viewer = BuiltInRoles.ViewerId.ToString();
        async Task<RpcException> RefusedAsync(Proto.CreateAccessKeyRequest request)
        {
            var refused = await Assert.ThrowsAsync<RpcException>(async () => await keys.CreateAsync(request));
            Assert.Equal(StatusCode.InvalidArgument, refused.StatusCode);
            return refused;
        }

        var empty = await RefusedAsync(new() { Name = " ", Days = 90, RoleId = viewer, AllDevices = true });
        Assert.Equal(("name", AccessKeyTexts.EnterName), (FieldOf(empty), empty.Status.Detail));
        var role = await RefusedAsync(new() { Name = "a", Days = 90, RoleId = Guid.NewGuid().ToString(), AllDevices = true });
        Assert.Equal(("role", AccessKeyTexts.UnknownRole), (FieldOf(role), role.Status.Detail));
        var noRole = await RefusedAsync(new() { Name = "a", Days = 90, AllDevices = true });
        Assert.Equal(("role", AccessKeyTexts.ChooseRole), (FieldOf(noRole), noRole.Status.Detail));
        var noDevices = await RefusedAsync(new() { Name = "a", Days = 90, RoleId = viewer });
        Assert.Equal("devices", FieldOf(noDevices));
        var unknownGroup = await RefusedAsync(new() { Name = "a", Days = 90, RoleId = viewer, DeviceGroupId = Guid.NewGuid().ToString() });
        Assert.Equal(("devices", Oadm.Core.DeviceGroups.DeviceGroupTexts.UnknownGroup), (FieldOf(unknownGroup), unknownGroup.Status.Detail));
        var days = await RefusedAsync(new() { Name = "a", Days = 45, RoleId = viewer, AllDevices = true });
        Assert.Equal(("days", AccessKeyTexts.ChooseDays), (FieldOf(days), days.Status.Detail));

        var created = await keys.CreateAsync(new() { Name = "Lobby-bot", Days = 365, RoleId = viewer, AllDevices = true });
        Assert.StartsWith(AccessKeyStore.Prefix, created.Secret, StringComparison.Ordinal);
        Assert.Equal(365, (int)Math.Round((created.Key.Expires.ToDateTime() - created.Key.Created.ToDateTime()).TotalDays));
        Assert.Equal(("Viewer", TestServerHost.AdminUserName), (created.Key.RoleName, created.Key.CreatedBy));
        var taken = await RefusedAsync(new() { Name = "LOBBY-BOT", Days = 90, RoleId = viewer, AllDevices = true });
        Assert.Equal(("name", AccessKeyTexts.NameTaken), (FieldOf(taken), taken.Status.Detail));
        Assert.Single((await keys.ListAsync(new Proto.Empty())).Keys);
    }

    [Fact]
    public async Task AnAdministratorKeyAlwaysHasEveryDevice()
    {
        await using var server = await McpTestServer.StartAsync();
        var (group, _) = await LobbyGroupAsync(server, devices: 1, others: 2);
        var created = await Keys(server.Host.Invoker).CreateAsync(new Proto.CreateAccessKeyRequest
        {
            Name = "admin-bot", Days = 30, RoleId = BuiltInRoles.AdministratorId.ToString(), DeviceGroupId = group.ToString(),
        });
        Assert.True(created.Key.AllDevices);
        Assert.Empty(created.Key.DeviceGroupId);
        Assert.Equal(3, (await server.ToolAsync(created.Secret, "oadm_devices_search"))["total"]!.GetValue<int>());
    }

    [Fact]
    public async Task AKeyIsAnIdentityOfItsOwnWithItsRoleAndDeviceGroupAndTheAdministratorIsAuditedForItsCreateAndDelete()
    {
        await using var server = await McpTestServer.StartAsync();
        var (group, _) = await LobbyGroupAsync(server, devices: 2, others: 3);
        var role = await RoleAsync(server, "Agents", HostPermissions.TagsAssign);
        var key = await server.CreateKeyAsync(name: "Lobby-bot", roleId: role.Id, groupId: group.ToString());

        // Its calls see the group's devices only and say who it is.
        Assert.Equal(2, (await server.ToolAsync(key, "oadm_devices_search"))["total"]!.GetValue<int>());
        var info = await server.ToolAsync(key, "oadm_server_info");
        Assert.Equal("MCP Lobby-bot", info["user"]!.GetValue<string>());
        Assert.Equal("Agents", info["role"]!.GetValue<string>());
        Assert.Equal("Lobby group", info["devices"]!["scope"]!.GetValue<string>());
        Assert.Equal(2, info["devices"]!["count"]!.GetValue<int>());

        // A change it makes is audited as the agent.
        var preview = await server.ToolAsync(key, "oadm_tags_assign_preview", new JsonObject { ["add"] = new JsonArray("Checked") });
        await server.ToolAsync(key, "oadm_tags_assign_run", new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() });
        var audit = (await server.Host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        Assert.Contains(audit, e => e.Action == AuditActions.DevicesTagged && e.UserName == "MCP Lobby-bot");
        Assert.Contains(audit, e => e.Action == AuditActions.AccessKeyCreated && e.Target == "Lobby-bot" && e.UserName == TestServerHost.AdminUserName
            && e.Detail.Contains("Agents", StringComparison.Ordinal) && e.Detail.Contains("Lobby group", StringComparison.Ordinal));

        // Delete ends it at once, audited by the administrator.
        var keys = Keys(server.Host.Invoker);
        var listed = Assert.Single((await keys.ListAsync(new Proto.Empty())).Keys);
        Assert.Equal(("Agents", "Lobby group", false), (listed.RoleName, listed.DeviceGroupName, listed.AllDevices));
        Assert.NotNull(listed.LastUsed);
        Assert.Empty((await keys.DeleteAsync(new Proto.AccessKeyIdRequest { Id = listed.Id })).Keys);
        Assert.Equal(401, (await server.PostAsync(key, McpTestServer.Request("tools/list"))).Status);
        var again = await Assert.ThrowsAsync<RpcException>(async () => await keys.DeleteAsync(new Proto.AccessKeyIdRequest { Id = listed.Id }));
        Assert.Equal((StatusCode.NotFound, AccessKeyTexts.NotFound), (again.StatusCode, again.Status.Detail));
        audit = (await server.Host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        Assert.Contains(audit, e => e.Action == AuditActions.AccessKeyDeleted && e.Target == "Lobby-bot" && e.UserName == TestServerHost.AdminUserName);
    }

    [Fact]
    public async Task ARoleChangeAndNewGroupRulesApplyToTheNextCall()
    {
        await using var server = await McpTestServer.StartAsync();
        var (group, tagId) = await LobbyGroupAsync(server, devices: 1, others: 2);
        var role = await RoleAsync(server, "Agents");
        var key = await server.CreateKeyAsync(name: "bot", roleId: role.Id, groupId: group.ToString());
        Assert.DoesNotContain("oadm_tags_assign_preview", await server.ToolNamesAsync(key));
        Assert.Equal(1, (await server.ToolAsync(key, "oadm_devices_search"))["total"]!.GetValue<int>());

        await new Proto.RoleService.RoleServiceClient(server.Host.Invoker).SaveRolesAsync(new Proto.SaveRolesRequest
        {
            Roles = { new Proto.RoleChange { Id = role.Id, Name = "Agents", Permissions = { HostPermissions.TagsAssign } } },
        });
        Assert.Contains("oadm_tags_assign_preview", await server.ToolNamesAsync(key));

        // The group now covers this server's devices too: all three.
        await new Proto.DeviceGroupService.DeviceGroupServiceClient(server.Host.Invoker).SaveAsync(new Proto.SaveDeviceGroupRequest
        {
            Id = group.ToString(), Name = "Lobby group", Rules = new Proto.DeviceGroupRules { ThisServer = true, TagIds = { tagId } },
        });
        Assert.Equal(3, (await server.ToolAsync(key, "oadm_devices_search"))["total"]!.GetValue<int>());
    }

    [Fact]
    public async Task ARoleOrDeviceGroupAKeyNamesIsNotDeletedAndTheRefusalNamesTheKey()
    {
        await using var server = await McpTestServer.StartAsync();
        var (group, _) = await LobbyGroupAsync(server, devices: 1, others: 0);
        var role = await RoleAsync(server, "Agents");
        var groupUser = await GroupCallers.UserAsync(server.Host, "lobby-tech", group);
        await server.CreateKeyAsync(name: "Lobby-bot", roleId: role.Id, groupId: group.ToString());

        var roles = new Proto.RoleService.RoleServiceClient(server.Host.Invoker);
        var roleRefused = await Assert.ThrowsAsync<RpcException>(async () => await roles.DeleteRoleAsync(new Proto.RoleIdRequest { Id = role.Id }));
        Assert.Equal((StatusCode.FailedPrecondition, AccessKeyTexts.RoleInUse("Agents", ["Lobby-bot"])), (roleRefused.StatusCode, roleRefused.Status.Detail));
        Assert.Equal("The access key Lobby-bot has the role Agents. Delete the key first.", roleRefused.Status.Detail);

        var groups = new Proto.DeviceGroupService.DeviceGroupServiceClient(server.Host.Invoker);
        var groupRefused = await Assert.ThrowsAsync<RpcException>(async () => await groups.DeleteAsync(new Proto.DeviceGroupIdRequest { Id = group.ToString() }));
        Assert.Equal((StatusCode.FailedPrecondition, AccessKeyTexts.GroupInUse("Lobby group", ["Lobby-bot"])), (groupRefused.StatusCode, groupRefused.Status.Detail));
        var user = (await server.Host.Users.ListAsync(new Proto.Empty())).Users.Single(u => u.UserName == "lobby-tech");
        Assert.Equal(group.ToString(), user.DeviceGroupId); // nothing changed: the group's users keep it
        Assert.Equal(groupUser.UserId.ToString(), user.Id);

        // With the key gone both can be deleted.
        var keys = Keys(server.Host.Invoker);
        await keys.DeleteAsync(new Proto.AccessKeyIdRequest { Id = Assert.Single((await keys.ListAsync(new Proto.Empty())).Keys).Id });
        await groups.DeleteAsync(new Proto.DeviceGroupIdRequest { Id = group.ToString() });
        await roles.DeleteRoleAsync(new Proto.RoleIdRequest { Id = role.Id });
    }

    [Fact]
    public void TheRefusalsNameEveryKey()
    {
        Assert.Equal("The access keys A, B and C have the role R. Delete the keys first.", AccessKeyTexts.RoleInUse("R", ["A", "B", "C"]));
        Assert.Equal("The access keys A and B have the device group G. Delete the keys first.", AccessKeyTexts.GroupInUse("G", ["A", "B"]));
    }
}
