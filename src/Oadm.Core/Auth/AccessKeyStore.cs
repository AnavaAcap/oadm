using System.Collections.Concurrent;
using System.Security.Cryptography;

using Microsoft.EntityFrameworkCore;

using Oadm.Core.DeviceGroups;
using Oadm.Core.Persistence;

namespace Oadm.Core.Auth;

/// <summary>An access key as the administrators see it (never the key or its hash).</summary>
/// <param name="Id">The key's identity (<see cref="AccessKeyEntity.Id"/>), the id of Delete.</param>
/// <param name="RoleName">The role's name now.</param>
/// <param name="DeviceGroupName">The device group's name now; empty with all devices.</param>
/// <param name="CreatedBy">The administrator who made it.</param>
public sealed record AccessKeyInfo(Guid Id, string Name, Guid RoleId, string RoleName, DeviceScope Devices, string DeviceGroupName, string CreatedBy,
    DateTime CreatedUtc, DateTime ExpiresUtc, DateTime? LastUsedUtc);

/// <summary>A valid access key: who the agent is now (the role and devices as stored at this call).</summary>
/// <param name="Id">The key's identity: the caller's user id of the call.</param>
/// <param name="TokenHash">SHA-256 of the key: what cursors and handles are bound to.</param>
public sealed record AccessKeySession(Guid Id, string TokenHash, string Name, Guid RoleId, DeviceScope Devices, DateTime ExpiresUtc)
{
    /// <summary>The name of the agent in the audit log and as task owner: "MCP Lobby-bot".</summary>
    public string AgentName => AccessKeyStore.AgentNameOf(Name);
}

/// <summary>A key that ended: its identity and hash (its jobs end with it).</summary>
public sealed record EndedAccessKey(Guid Id, string TokenHash);

/// <summary>
/// The access keys of AI agents (table AccessKeys, docs/spec/mcp.md "Access keys"). An administrator makes a key with a name, a
/// role, the devices (all, or one device group) and 30, 90 or 365 days; OADM hands the key out once and keeps only its SHA-256.
/// A key is an identity of its own, never a login of a user: its calls run with its role and devices as stored at each call,
/// so a role change or new group rules apply to the next call. A key ends when it is deleted or expires; its role and group
/// cannot be deleted while it names them (<see cref="RoleStore.DeleteAsync"/>, <see cref="DeviceGroupStore.DeleteAsync"/>);
/// a restore ends every key. Works only on the MCP server, never as a login session.
/// </summary>
public sealed class AccessKeyStore(IDbContextFactory<OadmDbContext> dbFactory, TimeProvider time) : IDisposable
{
    /// <summary>The start of every access key: an agent's configuration shows what the secret is.</summary>
    public const string Prefix = "oadm_";

    /// <summary>Longest name of an access key.</summary>
    public const int MaxNameLength = 64;

    /// <summary>Access keys on the server (a few per agent; the spec names no number).</summary>
    public const int MaxKeys = 100;

    /// <summary>The start of an agent's name in the audit log and the task owner.</summary>
    public const string AgentPrefix = "MCP ";

    /// <summary>The lifetimes an administrator may choose for an access key, in days.</summary>
    public static IReadOnlyList<int> Days { get; } = [30, 90, 365];

    public const int DefaultDays = 90;

    private static readonly TimeSpan PersistEvery = TimeSpan.FromMinutes(1);

    // Creating keys one at a time: the name check and the limit see every key made before (a few a day; one gate is enough).
    private readonly SemaphoreSlim _creating = new(1, 1);

    // The last use per key hash: in memory at once, in the database at most once a minute.
    private readonly ConcurrentDictionary<string, Use> _uses = new(StringComparer.Ordinal);

    /// <summary>A key ended: deleted (one) or a restore (<see cref="DeleteAllAsync"/>, every key).</summary>
    public event EventHandler<EndedAccessKey>? Ended;

    /// <summary>"MCP Lobby-bot": the agent in the audit log and as task owner.</summary>
    public static string AgentNameOf(string keyName) => AgentPrefix + CleanName(keyName);

    /// <summary>
    /// The key of <paramref name="key"/> with its role and devices of now, or null when it is not shaped like a key, unknown,
    /// deleted or ended. The last use is stored at most once a minute.
    /// </summary>
    public async Task<AccessKeySession?> ValidateAsync(string? key, CancellationToken ct)
    {
        if (!IsShaped(key))
        {
            return null;
        }

        var hash = AuthTokenStore.HashOf(key);
        var now = time.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.AccessKeys.AsNoTracking().FirstOrDefaultAsync(k => k.TokenHash == hash, ct).ConfigureAwait(false);
        if (row is null || row.ExpiresUtc <= now)
        {
            return null; // an ended key keeps its row until the hourly cleanup, so its refusals name it (NameOfRefusedKeyAsync)
        }

        var use = _uses.AddOrUpdate(hash, _ => new Use(now, row.LastUsedUtc), (_, old) => old with { Last = now });
        if (use.Persisted is not { } persisted || now - persisted >= PersistEvery)
        {
            _uses[hash] = use with { Persisted = now };
            await db.AccessKeys.Where(k => k.TokenHash == hash)
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedUtc, now), ct).ConfigureAwait(false);
        }

        return new AccessKeySession(row.Id, hash, row.Name, row.RoleId, row.DeviceAccess, row.ExpiresUtc);
    }

    /// <summary>The name of a key that did not validate but whose row is still there (it ended); null for a key OADM does not know.</summary>
    public async Task<string?> NameOfRefusedKeyAsync(string? key, CancellationToken ct)
    {
        if (!IsShaped(key))
        {
            return null;
        }

        var hash = AuthTokenStore.HashOf(key);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.AccessKeys.AsNoTracking().Where(k => k.TokenHash == hash).Select(k => k.Name).FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The key <paramref name="id"/> as stored (a queued task of an agent leaving the queue, the agent's own info); null when it
    /// is deleted or ended.
    /// </summary>
    public async Task<AccessKeySession?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.AccessKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Id == id, ct).ConfigureAwait(false);
        return row is null || row.ExpiresUtc <= time.GetUtcNow().UtcDateTime
            ? null
            : new AccessKeySession(row.Id, row.TokenHash, row.Name, row.RoleId, row.DeviceAccess, row.ExpiresUtc);
    }

    /// <summary>
    /// A new access key; returns the key (only now in clear text: OADM keeps its SHA-256 only) and what the list shows.
    /// <see cref="AccessKeyFieldException"/> with the field and its text (name, role, devices, days), or
    /// <see cref="InvalidOperationException"/> at <see cref="MaxKeys"/> keys. An Administrator key always has all devices.
    /// <paramref name="notAfterUtc"/>: the latest end (a key an agent makes never outlives the agent's own key).
    /// </summary>
    public async Task<(string Key, AccessKeyInfo Info)> CreateAsync(string? name, Guid roleId, DeviceScope devices, int days, string createdBy, CancellationToken ct,
        DateTime? notAfterUtc = null)
    {
        ArgumentNullException.ThrowIfNull(createdBy);
        var clean = NameError(name) is { } nameError ? throw new AccessKeyFieldException(AccessKeyField.Name, nameError) : name!.Trim();
        if (!Days.Contains(days))
        {
            throw new AccessKeyFieldException(AccessKeyField.Days, AccessKeyTexts.ChooseDays);
        }

        if (BuiltInRoles.IsAdministrator(roleId))
        {
            devices = DeviceScope.All; // an administrator always has every device
        }
        else if (!devices.AllDevices && devices.GroupId is null)
        {
            throw new AccessKeyFieldException(AccessKeyField.Devices, DeviceGroupTexts.ChooseDevices);
        }

        await _creating.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var role = await db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == roleId, ct).ConfigureAwait(false)
                ?? throw new AccessKeyFieldException(AccessKeyField.Role, AccessKeyTexts.UnknownRole);
            var groupName = string.Empty;
            if (!devices.AllDevices)
            {
                groupName = await db.DeviceGroups.AsNoTracking().Where(g => g.Id == devices.GroupId).Select(g => g.Name).FirstOrDefaultAsync(ct).ConfigureAwait(false)
                    ?? throw new AccessKeyFieldException(AccessKeyField.Devices, DeviceGroupTexts.UnknownGroup);
            }

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
            var now = time.GetUtcNow().UtcDateTime;
            var entity = new AccessKeyEntity
            {
                Id = Guid.NewGuid(),
                TokenHash = AuthTokenStore.HashOf(key),
                Name = clean,
                NormalizedName = normalized,
                RoleId = role.Id,
                AllDevices = devices.AllDevices,
                DeviceGroupId = devices.AllDevices ? null : devices.GroupId,
                CreatedBy = createdBy.Length > UserStore.MaxUserNameLength ? createdBy[..UserStore.MaxUserNameLength] : createdBy,
                CreatedUtc = now,
                ExpiresUtc = notAfterUtc is { } latest && latest < now.AddDays(days) ? latest : now.AddDays(days),
            };
            db.AccessKeys.Add(entity);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return (key, new AccessKeyInfo(entity.Id, clean, role.Id, role.Name, entity.DeviceAccess, groupName, entity.CreatedBy, now, entity.ExpiresUtc, null));
        }
        finally
        {
            _creating.Release();
        }
    }

    /// <summary>Every access key by name, with the names of its role and device group now.</summary>
    public async Task<IReadOnlyList<AccessKeyInfo>> ListAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await (from k in db.AccessKeys.AsNoTracking()
                          join r in db.Roles.AsNoTracking() on k.RoleId equals r.Id
                          join g in db.DeviceGroups.AsNoTracking() on k.DeviceGroupId equals g.Id into groups
                          from g in groups.DefaultIfEmpty()
                          select new { Key = k, RoleName = r.Name, GroupName = g == null ? null : g.Name }).ToListAsync(ct).ConfigureAwait(false);
        return [.. rows
            .OrderBy(r => r.Key.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => new AccessKeyInfo(r.Key.Id, r.Key.Name, r.Key.RoleId, r.RoleName, r.Key.DeviceAccess, r.GroupName ?? string.Empty, r.Key.CreatedBy,
                r.Key.CreatedUtc, r.Key.ExpiresUtc, LastUse(r.Key)))];
    }

    /// <summary>Deletes the key <paramref name="id"/> and ends it at once; returns its name, null for an unknown id.</summary>
    public async Task<string?> DeleteAsync(Guid id, CancellationToken ct)
    {
        AccessKeyEntity? row;
        await using (var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            row = await db.AccessKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Id == id, ct).ConfigureAwait(false);
            if (row is null || await db.AccessKeys.Where(k => k.Id == id).ExecuteDeleteAsync(ct).ConfigureAwait(false) == 0)
            {
                return null;
            }
        }

        _uses.TryRemove(row.TokenHash, out _);
        CallerContext.Raise(Ended, this, new EndedAccessKey(row.Id, row.TokenHash));
        return row.Name;
    }

    /// <summary>Deletes the keys that ended (hourly); their jobs end with them.</summary>
    public async Task<int> DeleteExpiredAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var ended = await db.AccessKeys.AsNoTracking().Where(k => k.ExpiresUtc <= now).Select(k => new { k.Id, k.TokenHash }).ToListAsync(ct).ConfigureAwait(false);
        if (ended.Count == 0)
        {
            return 0;
        }

        var deleted = await db.AccessKeys.Where(k => k.ExpiresUtc <= now).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        foreach (var key in ended)
        {
            _uses.TryRemove(key.TokenHash, out _);
            CallerContext.Raise(Ended, this, new EndedAccessKey(key.Id, key.TokenHash));
        }

        return deleted;
    }

    /// <summary>Ends every key (a restore: backups hold no keys).</summary>
    public async Task DeleteAllAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var all = await db.AccessKeys.AsNoTracking().Select(k => new { k.Id, k.TokenHash }).ToListAsync(ct).ConfigureAwait(false);
        await db.AccessKeys.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        _uses.Clear();
        foreach (var key in all)
        {
            CallerContext.Raise(Ended, this, new EndedAccessKey(key.Id, key.TokenHash));
        }
    }

    /// <summary>The names of the keys with the role <paramref name="roleId"/> (a role a key names is not deleted).</summary>
    internal static async Task<IReadOnlyList<string>> NamesWithRoleAsync(OadmDbContext db, Guid roleId, CancellationToken ct) =>
        [.. (await db.AccessKeys.AsNoTracking().Where(k => k.RoleId == roleId).Select(k => k.Name).ToListAsync(ct).ConfigureAwait(false))
            .Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The names of the keys with the device group <paramref name="groupId"/> (a group a key names is not deleted).</summary>
    internal static async Task<IReadOnlyList<string>> NamesWithGroupAsync(OadmDbContext db, Guid groupId, CancellationToken ct) =>
        [.. (await db.AccessKeys.AsNoTracking().Where(k => k.DeviceGroupId == groupId).Select(k => k.Name).ToListAsync(ct).ConfigureAwait(false))
            .Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The refusal of a name below the Name field, null when it is fine.</summary>
    public static string? NameError(string? name)
    {
        var clean = name?.Trim() ?? string.Empty;
        return clean.Length == 0 ? AccessKeyTexts.EnterName
            : clean.Length > MaxNameLength ? AccessKeyTexts.NameTooLong
            : clean.Any(char.IsControl) ? AccessKeyTexts.NameControl
            : clean.Any(c => !IsNameCharacter(c)) ? AccessKeyTexts.NameCharacters
            : null;
    }

    /// <summary>Letters, digits, space, '-', '_' and '.': the characters a key name may hold (it ends up in the audit log and the task owner).</summary>
    public static bool IsNameCharacter(char c) => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.';

    /// <summary><paramref name="name"/> with every other character replaced by '_', for texts that name the key.</summary>
    public static string CleanName(string? name) =>
        new string([.. (name ?? string.Empty).Trim().Take(MaxNameLength).Select(c => IsNameCharacter(c) ? c : '_')]);

    private DateTime? LastUse(AccessKeyEntity key) =>
        _uses.TryGetValue(key.TokenHash, out var use) && (key.LastUsedUtc is not { } stored || use.Last > stored) ? use.Last : key.LastUsedUtc;

    private static bool IsShaped([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? key) =>
        key is { Length: > 5 and <= 256 } && key.StartsWith(Prefix, StringComparison.Ordinal);

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose() => _creating.Dispose();

    private sealed record Use(DateTime Last, DateTime? Persisted);
}

/// <summary>The field of the create form a refusal belongs to.</summary>
public enum AccessKeyField
{
    Name,
    Role,
    Devices,
    Days,
}

/// <summary>A create refused for one field; the message is the text below it.</summary>
public sealed class AccessKeyFieldException : Exception
{
    public AccessKeyFieldException()
    {
    }

    public AccessKeyFieldException(string message)
        : base(message)
    {
    }

    public AccessKeyFieldException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public AccessKeyFieldException(AccessKeyField field, string message)
        : base(message) => Field = field;

    public AccessKeyField Field { get; }
}

/// <summary>The server's texts of access keys: the refusals the client shows below the field, the MCP server's 401.</summary>
public static class AccessKeyTexts
{
    public const string EnterName = "Enter a name.";
    public const string NameTooLong = "Use at most 64 characters.";
    public const string NameControl = "The name cannot contain line breaks or tabs.";
    public const string NameCharacters = "Use only letters, digits, spaces, - _ and . in the name.";
    public const string NameTaken = "An access key with this name exists.";
    public const string ChooseDays = "Choose 30, 90 or 365 days.";
    public const string ChooseRole = "Choose a role.";
    public const string UnknownRole = "The role does not exist (any more).";
    public const string TooMany = "Use at most 100 access keys. Delete one first.";
    public const string NotFound = "The access key no longer exists.";

    /// <summary>An agent's own key: it never deletes it (an administrator does in OADM, like a user's own account).</summary>
    public const string OwnKey = "An agent cannot delete its own access key. Delete it in OADM.";

    /// <summary>The refusal to delete a role keys have: "The access key Lobby-bot has the role Agents. Delete the key first."</summary>
    public static string RoleInUse(string roleName, IReadOnlyList<string> keyNames) =>
        $"{Naming(keyNames)} the role {roleName}. Delete {(keyNames.Count == 1 ? "the key" : "the keys")} first.";

    /// <summary>The refusal to delete a device group keys use: "The access key Lobby-bot has the device group Lobby. Delete the key first."</summary>
    public static string GroupInUse(string groupName, IReadOnlyList<string> keyNames) =>
        $"{Naming(keyNames)} the device group {groupName}. Delete {(keyNames.Count == 1 ? "the key" : "the keys")} first.";

    /// <summary>"The access key A has", "The access keys A, B and C have".</summary>
    private static string Naming(IReadOnlyList<string> names) => names.Count == 1
        ? "The access key " + names[0] + " has"
        : "The access keys " + string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1] + " have";

    /// <summary>The answer of the MCP server to a missing or ended key (HTTP 401).</summary>
    public const string NotValid = "The access key is not valid. An administrator creates a new one in OADM on the page MCP & Assistant.";
}
