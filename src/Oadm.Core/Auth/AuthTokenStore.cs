using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;

using Oadm.Core.Persistence;
using Oadm.Sdk.Plugins;

namespace Oadm.Core.Auth;

/// <summary>A valid session: the user behind a token.</summary>
public sealed record AuthSession(string TokenHash, Guid UserId, string UserName, Guid RoleId, DateTime ExpiresUtc, DeviceScope DeviceAccess)
{
    public UserRole Role => BuiltInRoles.IsAdministrator(RoleId) ? UserRole.Admin : UserRole.Operator;

    /// <summary>The name of the access key; null for a login session.</summary>
    public string? AccessKeyName { get; init; }

    /// <summary>An access key for reading only.</summary>
    public bool ReadOnly { get; init; }
}

/// <summary>An access key as its user and the administrators see it (never the key or its hash).</summary>
/// <param name="Id">An opaque id of the key for Delete (<see cref="AuthTokenStore.AccessKeyIdOf"/>).</param>
public sealed record AccessKeyInfo(string Id, Guid UserId, string UserName, string Name, bool ReadOnly, DateTime CreatedUtc, DateTime ExpiresUtc, DateTime? LastUsedUtc);

/// <summary>A key that did not work but whose row is still there (it ended): its user and name for the audit log.</summary>
public sealed record AccessKeyOwner(string UserName, string Name);

/// <summary>
/// Login sessions and access keys (table AuthTokens). A token is 32 random bytes (base64url) handed to the client once; the
/// server keeps only its SHA-256. Sliding expiry: 8 hours, 30 days with "Remember me"; every use moves the expiry forward
/// (written to the database at most once per minute per token). Valid sessions are cached in memory, so a call costs
/// no database read; disabling or deleting a user or a new password revokes its sessions (<see cref="RevokeUserAsync"/>),
/// a new role is taken over by them (<see cref="SetRole"/>). An open stream of a session counts as use
/// (<see cref="HoldWhileStreaming"/>): the session does not expire while it has one (a Live View wall without clicks).
/// Access keys (docs/spec/mcp.md "Access keys", kind <see cref="AuthTokenKind.AccessKey"/>) live in the same table and cache
/// and end the same way (user disabled or deleted, new password); they have a fixed expiry, a name and Read only, and work
/// only on the MCP server (<see cref="ValidateAccessKeyAsync"/>), never as a login session.
/// </summary>
public sealed class AuthTokenStore(IDbContextFactory<OadmDbContext> dbFactory, TimeProvider time)
{
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);
    public static readonly TimeSpan RememberLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan PersistEvery = TimeSpan.FromMinutes(1);

    /// <summary>The start of every access key: an agent's configuration shows what the secret is.</summary>
    public const string AccessKeyPrefix = "oadm_";

    /// <summary>Longest name of an access key.</summary>
    public const int MaxAccessKeyNameLength = 64;

    /// <summary>Access keys per user (the credential list's limit; the spec names none).</summary>
    public const int MaxAccessKeysPerUser = 20;

    /// <summary>The lifetimes a user may choose for an access key, in days.</summary>
    public static IReadOnlyList<int> AccessKeyDays { get; } = [30, 90, 365];

    public const int DefaultAccessKeyDays = 90;

    private readonly ConcurrentDictionary<string, Cached> _cache = new(StringComparer.Ordinal);
    // Creating keys one at a time: the name check and the limit see every key made before (a few a day; one gate is enough).
    private static readonly SemaphoreSlim CreatingKeys = new(1, 1);

    // Open streams per session (token hash); guarded by itself.
    private readonly Dictionary<string, int> _streaming = new(StringComparer.Ordinal);

    // Counts revokes and role changes. A session read from the database while one happened may be stale: it is read again.
    private long _epoch;

    /// <summary>Lower-case hex SHA-256 of a token.</summary>
    public static string HashOf(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? string.Empty)));

    /// <summary>Creates a session for the user; returns the token (only now in clear text).</summary>
    public async Task<string> CreateAsync(UserEntity user, bool remember, string clientAddress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = time.GetUtcNow().UtcDateTime;
        var entity = new AuthTokenEntity
        {
            TokenHash = HashOf(token),
            UserId = user.Id,
            ClientAddress = clientAddress.Length > 64 ? clientAddress[..64] : clientAddress,
            Remember = remember,
            CreatedUtc = now,
            ExpiresUtc = now + Lifetime(remember),
        };
        await using (var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            db.AuthTokens.Add(entity);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        _cache[entity.TokenHash] = new Cached(user.Id, user.UserName, user.RoleId, user.DeviceAccess, remember, entity.ExpiresUtc, entity.ExpiresUtc);
        return token;
    }

    /// <summary>The session of a token, or null when it is unknown, expired, revoked, an access key or its user is disabled.</summary>
    public async Task<AuthSession?> ValidateAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 256)
        {
            return null;
        }

        return await ValidateHashAsync(HashOf(token), ct).ConfigureAwait(false);
    }

    /// <summary>The session of a token hash (a second check of a call that is already authorized).</summary>
    public Task<AuthSession?> ValidateHashAsync(string tokenHash, CancellationToken ct) => ValidateKindAsync(tokenHash, AuthTokenKind.Session, ct);

    /// <summary>
    /// The user behind an access key (<see cref="AuthSession.AccessKeyName"/>, <see cref="AuthSession.ReadOnly"/>), or null
    /// when it is no access key (a session token too), unknown, ended, deleted or its user is disabled. The role and the
    /// devices are the user's of now. The last use is stored at most once a minute.
    /// </summary>
    public async Task<AuthSession?> ValidateAccessKeyAsync(string? key, CancellationToken ct)
    {
        if (!IsAccessKeyShaped(key))
        {
            return null;
        }

        return await ValidateKindAsync(HashOf(key), AuthTokenKind.AccessKey, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The user and name of a key that did not validate but whose row is still there (it ended and the hourly cleanup has not
    /// run yet, or its user is disabled); null for a key OADM does not know.
    /// </summary>
    public async Task<AccessKeyOwner?> OwnerOfRefusedKeyAsync(string? key, CancellationToken ct)
    {
        if (!IsAccessKeyShaped(key))
        {
            return null;
        }

        var hash = HashOf(key);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await (from t in db.AuthTokens.AsNoTracking()
                         join u in db.Users.AsNoTracking() on t.UserId equals u.Id
                         where t.TokenHash == hash && t.Kind == AuthTokenKind.AccessKey
                         select new { u.UserName, t.Name }).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return row is null ? null : new AccessKeyOwner(row.UserName, row.Name);
    }

    /// <summary>
    /// A new access key of the user <paramref name="userId"/>; returns the key (only now in clear text: OADM keeps its SHA-256 only) and
    /// what the lists show. <see cref="ArgumentException"/> with the text for the field (the name, the days), or
    /// <see cref="InvalidOperationException"/> at <see cref="MaxAccessKeysPerUser"/> keys.
    /// </summary>
    public async Task<(string Key, AccessKeyInfo Info)> CreateAccessKeyAsync(Guid userId, string userName, string? name, int days, bool readOnly, string clientAddress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(userName);
        ArgumentNullException.ThrowIfNull(clientAddress);
        var clean = AccessKeyNameError(name) is { } nameError ? throw new ArgumentException(nameError) : name!.Trim();
        if (!AccessKeyDays.Contains(days))
        {
            throw new ArgumentException(AccessKeyTexts.ChooseDays);
        }

        await CreatingKeys.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var names = await db.AuthTokens.AsNoTracking().Where(t => t.UserId == userId && t.Kind == AuthTokenKind.AccessKey)
                .Select(t => t.Name).ToListAsync(ct).ConfigureAwait(false);
            if (names.Contains(clean, StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentException(AccessKeyTexts.NameTaken);
            }

            if (names.Count >= MaxAccessKeysPerUser)
            {
                throw new InvalidOperationException(AccessKeyTexts.TooMany);
            }

            var key = AccessKeyPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));
            var now = time.GetUtcNow().UtcDateTime;
            var entity = new AuthTokenEntity
            {
                TokenHash = HashOf(key),
                UserId = userId,
                ClientAddress = clientAddress.Length > 64 ? clientAddress[..64] : clientAddress,
                CreatedUtc = now,
                ExpiresUtc = now.AddDays(days),
                Kind = AuthTokenKind.AccessKey,
                Name = clean,
                ReadOnly = readOnly,
            };
            db.AuthTokens.Add(entity);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return (key, new AccessKeyInfo(AccessKeyIdOf(entity.TokenHash), userId, userName, clean, readOnly, now, entity.ExpiresUtc, null));
        }
        finally
        {
            CreatingKeys.Release();
        }
    }

    /// <summary>The access keys of one user (null: of every user, for administrators), by user name, then the newest first.</summary>
    public async Task<IReadOnlyList<AccessKeyInfo>> ListAccessKeysAsync(Guid? userId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await (from t in db.AuthTokens.AsNoTracking()
                          join u in db.Users.AsNoTracking() on t.UserId equals u.Id
                          where t.Kind == AuthTokenKind.AccessKey && (userId == null || t.UserId == userId)
                          select new { t.TokenHash, t.UserId, u.UserName, t.Name, t.ReadOnly, t.CreatedUtc, t.ExpiresUtc, t.LastUsedUtc }).ToListAsync(ct).ConfigureAwait(false);
        return [.. rows
            .OrderBy(r => r.UserName, StringComparer.OrdinalIgnoreCase).ThenByDescending(r => r.CreatedUtc)
            .Select(r => new AccessKeyInfo(AccessKeyIdOf(r.TokenHash), r.UserId, r.UserName, r.Name, r.ReadOnly, r.CreatedUtc, r.ExpiresUtc,
                _cache.TryGetValue(r.TokenHash, out var cached) && cached.LastUsedUtc > r.LastUsedUtc ? cached.LastUsedUtc : r.LastUsedUtc))];
    }

    /// <summary>
    /// Deletes the access key <paramref name="id"/> of <paramref name="userId"/> (null: of any user, for administrators) and
    /// ends it at once; returns what was deleted, null for an unknown id (also a key of another user).
    /// </summary>
    public async Task<AccessKeyInfo?> DeleteAccessKeyAsync(string id, Guid? userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        var keys = await ListAccessKeysAsync(userId, ct).ConfigureAwait(false);
        if (keys.FirstOrDefault(k => string.Equals(k.Id, id, StringComparison.Ordinal)) is not { } info)
        {
            return null;
        }

        List<string> hashes;
        await using (var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            hashes = await db.AuthTokens.AsNoTracking().Where(t => t.Kind == AuthTokenKind.AccessKey && t.UserId == info.UserId)
                .Select(t => t.TokenHash).ToListAsync(ct).ConfigureAwait(false);
        }

        if (hashes.FirstOrDefault(h => string.Equals(AccessKeyIdOf(h), id, StringComparison.Ordinal)) is not { } hash)
        {
            return null; // deleted meanwhile
        }

        await RevokeAsync(hash, ct).ConfigureAwait(false);
        return info;
    }

    /// <summary>The opaque id of an access key (Delete in the lists): never the key or the hash OADM stores.</summary>
    public static string AccessKeyIdOf(string tokenHash)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("oadm access key " + (tokenHash ?? string.Empty)));
        return Convert.ToHexStringLower(bytes.AsSpan(0, 16));
    }

    /// <summary>The refusal of a name below the Name field, null when it is fine.</summary>
    public static string? AccessKeyNameError(string? name)
    {
        var clean = name?.Trim() ?? string.Empty;
        return clean.Length == 0 ? AccessKeyTexts.EnterName
            : clean.Length > MaxAccessKeyNameLength ? AccessKeyTexts.NameTooLong
            : clean.Any(char.IsControl) ? AccessKeyTexts.NameControl
            : null;
    }

    private static bool IsAccessKeyShaped([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? key) =>
        key is { Length: > 5 and <= 256 } && key.StartsWith(AccessKeyPrefix, StringComparison.Ordinal);

    private async Task<AuthSession?> ValidateKindAsync(string hash, AuthTokenKind kind, CancellationToken ct)
    {
        while (true)
        {
            var (done, session) = await TryValidateAsync(hash, kind, ct).ConfigureAwait(false);
            if (done)
            {
                return session;
            }
        }
    }

    /// <summary>
    /// One attempt; not done when a revoke or role change ran at the same time (the cache entry changed under it), so a
    /// revoked session or an old role never goes back into the cache.
    /// </summary>
    private async Task<(bool Done, AuthSession? Session)> TryValidateAsync(string hash, AuthTokenKind kind, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var wasCached = true;
        if (!_cache.TryGetValue(hash, out var cached))
        {
            wasCached = false;
            var epoch = Interlocked.Read(ref _epoch);
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var row = await (from t in db.AuthTokens.AsNoTracking()
                             join u in db.Users.AsNoTracking() on t.UserId equals u.Id
                             where t.TokenHash == hash && !u.Disabled
                             select new { t.Remember, t.ExpiresUtc, t.Kind, t.Name, t.ReadOnly, t.LastUsedUtc, u.Id, u.UserName, u.RoleId, u.AllDevices, u.DeviceGroupId }).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (row is null)
            {
                return (true, null);
            }

            cached = new Cached(row.Id, row.UserName, row.RoleId, new DeviceScope(row.AllDevices, row.DeviceGroupId), row.Remember, row.ExpiresUtc, row.ExpiresUtc)
            {
                Kind = row.Kind,
                Name = row.Name,
                ReadOnly = row.ReadOnly,
                LastUsedUtc = row.LastUsedUtc,
                PersistedLastUsedUtc = row.LastUsedUtc,
            };
            _cache[hash] = cached;
            if (Interlocked.Read(ref _epoch) != epoch)
            {
                _cache.TryRemove(new KeyValuePair<string, Cached>(hash, cached));
                return (false, null);
            }
        }

        if (cached.Kind != kind)
        {
            return (true, null); // a session token never works as an access key, nor an access key as a session
        }

        if (cached.ExpiresUtc <= now && kind == AuthTokenKind.AccessKey)
        {
            // An ended access key keeps its row until the hourly cleanup (DeleteExpiredAsync), so its refusals name the key's
            // user and name in the audit log (OwnerOfRefusedKeyAsync); it never goes back into the cache.
            if (_cache.TryRemove(hash, out _) && wasCached)
            {
                CallerContext.Raise(SessionsEnded, this, new EndedSessions(null, hash));
            }

            return (true, null);
        }

        if (cached.ExpiresUtc <= now && !IsStreaming(hash))
        {
            if (_cache.TryRemove(hash, out _))
            {
                // Like a logout: what belongs to the session (its jobs, its add page sessions, its streams) ends too. Raised
                // before the row goes, and the row goes whatever the call's token says: a cancelled call never skips the end.
                CallerContext.Raise(SessionsEnded, this, new EndedSessions(null, hash));
            }

            await DeleteAsync(hash, CancellationToken.None).ConfigureAwait(false);
            return (true, null);
        }

        var original = cached;
        if (kind == AuthTokenKind.AccessKey)
        {
            // A fixed end; the last use in memory now, in the database at most once per minute.
            cached = cached with { LastUsedUtc = now };
            if (cached.PersistedLastUsedUtc is not { } persisted || now - persisted >= PersistEvery)
            {
                cached = cached with { PersistedLastUsedUtc = now };
                await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
                await db.AuthTokens.Where(t => t.TokenHash == hash)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedUtc, now), ct).ConfigureAwait(false);
            }

            if (!_cache.TryUpdate(hash, cached, original))
            {
                return (false, null);
            }

            return (true, new AuthSession(hash, cached.UserId, cached.UserName, cached.RoleId, cached.ExpiresUtc, cached.DeviceAccess)
            {
                AccessKeyName = cached.Name,
                ReadOnly = cached.ReadOnly,
            });
        }

        // Sliding expiry: in memory now, in the database at most once per minute.
        var expires = now + Lifetime(cached.Remember);
        cached = cached with { ExpiresUtc = expires };
        if (expires - cached.PersistedExpiresUtc >= PersistEvery)
        {
            cached = cached with { PersistedExpiresUtc = expires };
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await db.AuthTokens.Where(t => t.TokenHash == hash)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresUtc, expires), ct).ConfigureAwait(false);
        }

        if (!_cache.TryUpdate(hash, cached, original))
        {
            return (false, null);
        }

        return (true, new AuthSession(hash, cached.UserId, cached.UserName, cached.RoleId, expires, cached.DeviceAccess));
    }

    /// <summary>
    /// Sessions ended: one token (logout, or the session expired: found on its next use or by <see cref="DeleteExpiredAsync"/>)
    /// or every session of a user (password, disabled, deleted).
    /// </summary>
    public event EventHandler<EndedSessions>? SessionsEnded;

    /// <summary>Logout: ends one session.</summary>
    public async Task RevokeAsync(string tokenHash, CancellationToken ct)
    {
        // The rows go first, then the cache: a call that misses the cache meanwhile finds no row, or sees the new epoch.
        await DeleteAsync(tokenHash, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _epoch);
        _cache.TryRemove(tokenHash, out _);
        CallerContext.Raise(SessionsEnded, this, new EndedSessions(null, tokenHash));
    }

    /// <summary>The user's sessions take over a new role (sessions not cached read it from the database).</summary>
    public void SetRole(Guid userId, Guid roleId)
    {
        Interlocked.Increment(ref _epoch);
        foreach (var key in _cache.Where(p => p.Value.UserId == userId).Select(p => p.Key).ToList())
        {
            // A validation may replace the entry at the same moment (sliding expiry): try again until the role is in.
            while (_cache.TryGetValue(key, out var current) && current.RoleId != roleId
                && !_cache.TryUpdate(key, current with { RoleId = roleId }, current))
            {
            }
        }
    }

    /// <summary>The user's sessions take over new devices (all devices, another group, none after a deleted group).</summary>
    public void SetDeviceAccess(Guid userId, DeviceScope access)
    {
        Interlocked.Increment(ref _epoch);
        foreach (var key in _cache.Where(p => p.Value.UserId == userId).Select(p => p.Key).ToList())
        {
            while (_cache.TryGetValue(key, out var current) && current.DeviceAccess != access
                && !_cache.TryUpdate(key, current with { DeviceAccess = access }, current))
            {
            }
        }
    }

    /// <summary>Ends every session of a user (disabled, deleted, password changed).</summary>
    public async Task RevokeUserAsync(Guid userId, CancellationToken ct)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            await db.AuthTokens.Where(t => t.UserId == userId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }

        Interlocked.Increment(ref _epoch);
        foreach (var pair in _cache.Where(p => p.Value.UserId == userId).ToList())
        {
            _cache.TryRemove(pair.Key, out _);
        }

        CallerContext.Raise(SessionsEnded, this, new EndedSessions(userId, null));
    }

    /// <summary>
    /// Deletes expired sessions (hourly). Each expired session that was in use since the server started raises
    /// <see cref="SessionsEnded"/> (sessions not used since the start own nothing in memory). A session with an open stream
    /// is in use: its expiry slides and is stored instead, so a restart keeps it too.
    /// </summary>
    public async Task<int> DeleteExpiredAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var ended = new List<string>();
        var slid = new List<(string Hash, DateTime Expires)>();
        foreach (var pair in _cache.ToList())
        {
            if (IsStreaming(pair.Key))
            {
                var expires = now + Lifetime(pair.Value.Remember);
                if (_cache.TryUpdate(pair.Key, pair.Value with { ExpiresUtc = expires, PersistedExpiresUtc = expires }, pair.Value))
                {
                    slid.Add((pair.Key, expires));
                }
            }
            else if (pair.Value.ExpiresUtc <= now && _cache.TryRemove(pair))
            {
                ended.Add(pair.Key);
            }
        }

        foreach (var hash in ended)
        {
            CallerContext.Raise(SessionsEnded, this, new EndedSessions(null, hash));
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        foreach (var (hash, expires) in slid)
        {
            // One row per session with an open stream (a few), once an hour.
            await db.AuthTokens.Where(t => t.TokenHash == hash)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresUtc, expires), ct).ConfigureAwait(false);
        }

        // Sliding expiries newer than the stored value live in the cache; only rows expired in both are deleted.
        var live = _cache.Keys.ToList();
        return await db.AuthTokens.Where(t => t.ExpiresUtc <= now && !live.Contains(t.TokenHash)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// An open stream of the session of <paramref name="tokenHash"/> (<c>CallerStreams</c>): until the returned scope is
    /// disposed the session counts as in use and does not expire; when its last stream closes, the expiry slides from then.
    /// </summary>
    public IDisposable HoldWhileStreaming(string tokenHash)
    {
        ArgumentException.ThrowIfNullOrEmpty(tokenHash);
        lock (_streaming)
        {
            _streaming[tokenHash] = _streaming.GetValueOrDefault(tokenHash) + 1;
        }

        return new StreamHold(this, tokenHash);
    }

    private bool IsStreaming(string hash)
    {
        lock (_streaming)
        {
            return _streaming.ContainsKey(hash);
        }
    }

    private void ReleaseStream(string hash)
    {
        lock (_streaming)
        {
            var left = _streaming.GetValueOrDefault(hash) - 1;
            if (left > 0)
            {
                _streaming[hash] = left;
                return;
            }

            _streaming.Remove(hash);
        }

        // The stream was the last use: the session lives a full lifetime from now (in memory; stored on its next use).
        var now = time.GetUtcNow().UtcDateTime;
        while (_cache.TryGetValue(hash, out var current))
        {
            var expires = now + Lifetime(current.Remember);
            if (expires <= current.ExpiresUtc || _cache.TryUpdate(hash, current with { ExpiresUtc = expires }, current))
            {
                return;
            }
        }
    }

    private static TimeSpan Lifetime(bool remember) => remember ? RememberLifetime : SessionLifetime;

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task DeleteAsync(string hash, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.AuthTokens.Where(t => t.TokenHash == hash).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    private sealed class StreamHold(AuthTokenStore owner, string hash) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.ReleaseStream(hash);
            }
        }
    }

    private sealed record Cached(Guid UserId, string UserName, Guid RoleId, DeviceScope DeviceAccess, bool Remember, DateTime ExpiresUtc, DateTime PersistedExpiresUtc)
    {
        public AuthTokenKind Kind { get; init; }

        public string Name { get; init; } = string.Empty;

        public bool ReadOnly { get; init; }

        public DateTime? LastUsedUtc { get; init; }

        public DateTime? PersistedLastUsedUtc { get; init; }
    }
}

/// <summary>The server's texts of access keys: the refusals the client shows below the field, the MCP server's 401.</summary>
public static class AccessKeyTexts
{
    public const string EnterName = "Enter a name.";
    public const string NameTooLong = "Use at most 64 characters.";
    public const string NameControl = "The name cannot contain line breaks or tabs.";
    public const string NameTaken = "You already have an access key with this name.";
    public const string ChooseDays = "Choose 30, 90 or 365 days.";
    public const string TooMany = "Use at most 20 access keys. Delete one first.";
    public const string NotFound = "The access key no longer exists.";

    /// <summary>The answer of the MCP server to a missing or ended key (HTTP 401).</summary>
    public const string NotValid = "The access key is not valid. Create a new one in OADM under Access keys.";
}

/// <summary>Which sessions ended: every session of <see cref="UserId"/>, or the one of <see cref="TokenHash"/>.</summary>
public sealed record EndedSessions(Guid? UserId, string? TokenHash);
