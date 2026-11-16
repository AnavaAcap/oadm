using Microsoft.EntityFrameworkCore;

using Oadm.Core.Auth;
using Oadm.Core.Persistence;
using Oadm.Sdk.Devices;

namespace Oadm.Core.Tests.DeviceGroups;

public sealed class DelegatedConnectionTests : DeviceGroupTestBase
{
    private AccessKeyStore Keys => Db.Get<AccessKeyStore>();

    private static DelegationGrant Grant(Guid user, DelegatedDevice device) =>
        new(1, user, [device], [HostPermissions.TagsAssign], ["oadm_devices_search", "oadm_tags_assign_preview", "oadm_tags_assign_run"]);

    [Fact]
    public async Task AdministratorsDelegateOnlyTheExactPinnedDevicesAndAThirtyDayCeiling()
    {
        var one = await AddDeviceAsync("ACCC8E000001");
        var two = await AddDeviceAsync("ACCC8E000002");
        var user = await Users.AddAsync("owner", "pw", BuiltInRoles.AdministratorId, DeviceScope.All, Ct);
        var caller = CallerOf(user) with { CanApprove = true };
        var before = DateTime.UtcNow;
        var (key, id, expires) = await Keys.CreateDelegatedAsync(caller, "Claude laptop", Grant(user.Id, new(one.Id, one.Serial)), await Views.ForAsync(caller, Ct), Ct);

        Assert.InRange(expires, before.AddDays(30), DateTime.UtcNow.AddDays(30));
        var session = await Keys.ValidateAsync(key, Ct);
        Assert.NotNull(session?.Delegation);
        Assert.False(session.Delegation.Intersect(PermissionSet.All).IsAll);
        var view = await Views.ForUserAsync(id, Ct);
        Assert.False(view.HasAllDevices);
        Assert.False(view.CoversRoute(null));
        Assert.StartsWith("connection:", view.ScopeKey, StringComparison.Ordinal);
        Assert.Equal(one.Id, Assert.Single(await view.ListAsync(Ct)).Id);
        Assert.Equal(one.Id, Assert.Single(await view.IdsAsync(Ct)));
        Assert.Null(await view.FindAsync(two.Id, Ct));
        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => view.GetAsync([one.Id, two.Id], Ct));
        Assert.False(view.Contains(new Oadm.Core.Devices.Device { Id = one.Id, Serial = "ACCC8E999999" }));
        Assert.False(view.Contains(new Oadm.Core.Devices.Device { Id = Guid.NewGuid(), Serial = one.Serial }));
        Assert.Null(view.CachedIds());

        await using var db = await Db.Get<IDbContextFactory<OadmDbContext>>().CreateDbContextAsync(Ct);
        var stored = await db.AccessKeys.SingleAsync(k => k.Id == id, Ct);
        Assert.Equal(AuthTokenStore.HashOf(key), stored.TokenHash);
        Assert.DoesNotContain(key, stored.DelegationJson!, StringComparison.Ordinal);
        Assert.False(stored.AllDevices);
        Assert.Equal(user.Id, (await Keys.GetAsync(id, Ct))!.Delegation!.OwnerId);
    }

    [Fact]
    public async Task LiveGroupRoleDisabledUserAndKeyRevocationRemoveAccess()
    {
        await Db.Get<RoleStore>().SyncCatalogAsync(PermissionCatalog.HostOnly, Ct);
        var one = await AddDeviceAsync("ACCC8E000001");
        var two = await AddDeviceAsync("ACCC8E000002");
        var group = (await SaveGroupAsync("selected", serials: [one.Serial, two.Serial])).Group;
        var user = await Users.AddAsync("operator", "pw", BuiltInRoles.OperatorId, DeviceScope.Group(group.Id), Ct);
        var caller = CallerOf(user) with { CanApprove = true };
        var (key, id, _) = await Keys.CreateDelegatedAsync(caller, "operator laptop", Grant(user.Id, new(one.Id, one.Serial)), await Views.ForAsync(caller, Ct), Ct);
        var view = await Views.ForUserAsync(id, Ct);
        Assert.Equal(one.Id, Assert.Single(await view.ListAsync(Ct)).Id);
        await view.RequireCurrentAsync(id, HostPermissions.TagsAssign, one.Id, one.Serial, Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => view.RequireCurrentAsync(id, "example.plugin:install", one.Id, one.Serial, Ct));

        await SaveGroupAsync("selected", serials: [two.Serial], id: group.Id, revision: group.Revision);
        Assert.Empty(await view.ListAsync(Ct));
        Assert.Empty(await view.IdsAsync(Ct));
        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => view.RequireCurrentAsync(id, HostPermissions.TagsAssign, one.Id, one.Serial, Ct));
        await Users.UpdateAsync(user.Id, BuiltInRoles.ViewerId, null, null, null, Ct);
        var session = (await Keys.ValidateAsync(key, Ct))!;
        var rights = await Db.Get<RoleStore>().PermissionsOfAsync(session.RoleId, Ct);
        Assert.False(session.Delegation!.Intersect(rights).Has(HostPermissions.TagsAssign));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => view.RequireCurrentAsync(id, HostPermissions.TagsAssign, one.Id, one.Serial, Ct));
        await Users.UpdateAsync(user.Id, (Guid?)null, disabled: true, null, null, Ct);
        Assert.Null(await Keys.ValidateAsync(key, Ct));
        Assert.Empty(await (await Views.ForUserAsync(id, Ct)).ListAsync(Ct));
        await Keys.DeleteAsync(id, Ct);
        Assert.Null(await Keys.GetAsync(id, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => view.RequireCurrentAsync(id, HostPermissions.TagsAssign, one.Id, one.Serial, Ct));
    }

    [Fact]
    public async Task ToolsOtherUsersAndChangedDeviceIdentityCannotAuthorizeAConnection()
    {
        var one = await AddDeviceAsync("ACCC8E000001");
        var user = await Users.AddAsync("owner", "pw", BuiltInRoles.AdministratorId, DeviceScope.All, Ct);
        var caller = CallerOf(user);
        var view = await Views.ForAsync(caller, Ct);
        var grant = Grant(user.Id, new(one.Id, one.Serial));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Keys.CreateDelegatedAsync(caller, "tool", grant, view, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Keys.CreateDelegatedAsync(caller with { CanApprove = true, IsAccessKey = true }, "key", grant, view, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Keys.CreateDelegatedAsync(caller with { CanApprove = true }, "foreign", grant with { OwnerId = Guid.NewGuid() }, view, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Keys.CreateDelegatedAsync(caller with { CanApprove = true }, "changed", Grant(user.Id, new(one.Id, "ACCC8E999999")), view, Ct));
        Assert.Empty(await Keys.ListAsync(Ct));
    }

    [Fact]
    public async Task CorruptOrUnknownGrantsAndExpiredKeysFailClosed()
    {
        var one = await AddDeviceAsync("ACCC8E000001");
        var user = await Users.AddAsync("owner", "pw", BuiltInRoles.AdministratorId, DeviceScope.All, Ct);
        var caller = CallerOf(user) with { CanApprove = true };
        var (key, id, _) = await Keys.CreateDelegatedAsync(caller, "laptop", Grant(user.Id, new(one.Id, one.Serial)), await Views.ForAsync(caller, Ct), Ct);
        await using var db = await Db.Get<IDbContextFactory<OadmDbContext>>().CreateDbContextAsync(Ct);
        var row = await db.AccessKeys.SingleAsync(k => k.Id == id, Ct);
        var original = row.DelegationJson;
        foreach (var corrupt in new[] { "{", "null", "{}", original!.Replace("\"Version\":1", "\"Version\":99", StringComparison.Ordinal) })
        {
            row.DelegationJson = corrupt;
            await db.SaveChangesAsync(Ct);
            Assert.Null(await Keys.ValidateAsync(key, Ct));
            Assert.Null(await Keys.GetAsync(id, Ct));
        }

        row.DelegationJson = original;
        row.ExpiresUtc = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync(Ct);
        Assert.Null(await Keys.ValidateAsync(key, Ct));
        Assert.Empty(await (await Views.ForUserAsync(id, Ct)).ListAsync(Ct));
    }
}
