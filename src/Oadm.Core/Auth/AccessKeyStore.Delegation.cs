using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Oadm.Core.Persistence;

namespace Oadm.Core.Auth;

public sealed partial class AccessKeyStore
{
    /// <summary>
    /// Records a grant already reviewed in the authenticated client. No custom role or user is created. Only the key
    /// hash is persisted; the returned credential must go directly to the connector's single-use exchange.
    /// The caller supplies its current device view after checking every pin, never a caller-controlled all-devices flag.
    /// </summary>
    public async Task<(string Key, Guid Id, DateTime ExpiresUtc)> CreateDelegatedAsync(AuthenticatedCaller caller,
        string name, DelegationGrant grant, DeviceGroups.DeviceView view, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(view);
        grant = DelegationGrant.Read(JsonSerializer.Serialize(grant))
            ?? throw new UnauthorizedAccessException("Review the connection's devices and capabilities again.");
        if (!caller.CanApprove || caller.IsAccessKey || !grant.IsValid || grant.OwnerId != caller.UserId)
        {
            throw new UnauthorizedAccessException("Connect this computer in OADM's authenticated client.");
        }

        if (NameError(name) is { } error)
        {
            throw new AccessKeyFieldException(AccessKeyField.Name, error);
        }

        var selected = await view.GetAsync(grant.Devices.Select(d => d.Id).ToArray(), ct).ConfigureAwait(false);
        var pins = grant.DevicePins();
        if (selected.Any(d => !pins.TryGetValue(d.Id, out var serial) || !string.Equals(d.Serial, serial, StringComparison.Ordinal)))
        {
            throw new UnauthorizedAccessException("The selected devices changed. Review the connection again.");
        }

        await _creating.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var owner = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == grant.OwnerId && !u.Disabled, ct).ConfigureAwait(false)
                ?? throw new UnauthorizedAccessException("The authorizing user is no longer available.");
            var currentView = await view.Factory.ForUserAsync(owner.Id, ct).ConfigureAwait(false);
            await currentView.GetAsync(grant.Devices.Select(d => d.Id).ToArray(), ct).ConfigureAwait(false);
            var role = await db.Roles.AsNoTracking().SingleAsync(r => r.Id == owner.RoleId, ct).ConfigureAwait(false);
            var rights = role.Kind == RoleKind.Administrator ? PermissionSet.All
                : PermissionSet.Of(JsonSerializer.Deserialize<string[]>(role.PermissionsJson) ?? []);
            if (grant.Permissions.Any(p => !rights.Has(p) || !caller.Has(p)))
            {
                throw new UnauthorizedAccessException("Your current role does not allow the requested capabilities.");
            }

            var clean = name.Trim();
            var normalized = clean.ToUpperInvariant();
            if (await db.AccessKeys.AnyAsync(k => k.NormalizedName == normalized, ct).ConfigureAwait(false))
            {
                throw new AccessKeyFieldException(AccessKeyField.Name, AccessKeyTexts.NameTaken);
            }

            if (await db.AccessKeys.CountAsync(ct).ConfigureAwait(false) >= MaxKeys)
            {
                throw new InvalidOperationException(AccessKeyTexts.TooMany);
            }

            var key = Prefix + Base64Url(RandomNumberGenerator.GetBytes(32));
            var row = new AccessKeyEntity
            {
                Id = Guid.NewGuid(), TokenHash = AuthTokenStore.HashOf(key), Name = clean, NormalizedName = normalized,
                // This row has no independent authority. SessionAsync resolves the owner on every use. Even a lost
                // grant cannot turn an administrator's connection into an administrator access key.
                RoleId = BuiltInRoles.ViewerId, AllDevices = false, DeviceGroupId = null,
                CreatedBy = owner.UserName, CreatedUtc = time.GetUtcNow().UtcDateTime,
                ExpiresUtc = time.GetUtcNow().UtcDateTime.AddDays(DelegationGrant.DefaultDays),
                DelegationJson = JsonSerializer.Serialize(grant),
            };
            db.AccessKeys.Add(row);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return (key, row.Id, row.ExpiresUtc);
        }
        finally
        {
            _creating.Release();
        }
    }

    private static async Task<AccessKeySession?> SessionAsync(OadmDbContext db, AccessKeyEntity row, CancellationToken ct)
    {
        if (row.DelegationJson is null)
        {
            return new(row.Id, row.TokenHash, row.Name, row.RoleId, row.DeviceAccess, row.ExpiresUtc);
        }

        if (DelegationGrant.Read(row.DelegationJson) is not { } grant)
        {
            return null;
        }

        var owner = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == grant.OwnerId && !u.Disabled, ct).ConfigureAwait(false);
        return owner is null ? null : new AccessKeySession(row.Id, row.TokenHash, row.Name, owner.RoleId,
            BuiltInRoles.IsAdministrator(owner.RoleId) ? DeviceScope.All : owner.DeviceAccess, row.ExpiresUtc) { Delegation = grant };
    }
}
