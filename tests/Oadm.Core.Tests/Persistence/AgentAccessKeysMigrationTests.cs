using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

using Oadm.Core.Auth;
using Oadm.Core.Persistence;

namespace Oadm.Core.Tests.Persistence;

/// <summary>
/// Migration <c>AgentAccessKeys</c> (docs/spec/mcp.md "Access keys"): the access keys of the first design (rows of AuthTokens with
/// Kind 1, never released) are deleted, so an old key never works as a login session; login sessions stay; keys are now rows of
/// AccessKeys with a role and devices of their own.
/// </summary>
public sealed class AgentAccessKeysMigrationTests
{
    private const string Before = "20261114165052_AccessKeys";

    [Fact]
    public async Task TheOldKeysAreDeletedAndTheSessionsStay()
    {
        var dir = TestDatabase.NewTempDirectory();
        Directory.CreateDirectory(dir);
        try
        {
            var options = new DbContextOptionsBuilder<OadmDbContext>()
                .UseSqlite($"Data Source={Path.Combine(dir, "oadm.db")};Pooling=False")
                .Options;
            await using (var before = new OadmDbContext(options))
            {
                await before.GetService<IMigrator>().MigrateAsync(Before);
                await before.Database.ExecuteSqlRawAsync(
                    "INSERT INTO \"Users\" (\"Id\", \"UserName\", \"NormalizedName\", \"PasswordHash\", \"RoleId\", \"Disabled\", \"CreatedUtc\", \"AllDevices\") " +
                    "VALUES ('11111111-1111-1111-1111-111111111111', 'admin', 'ADMIN', 'x', '00000000-0000-0000-0000-000000000001', 0, '2026-01-01 00:00:00', 1)");
                foreach (var (hash, kind) in new[] { ("h-session", 0), ("h-key", 1) })
                {
                    await before.Database.ExecuteSqlRawAsync(
                        "INSERT INTO \"AuthTokens\" (\"TokenHash\", \"ClientAddress\", \"CreatedUtc\", \"ExpiresUtc\", \"Remember\", \"UserId\", \"Kind\", \"Name\", \"ReadOnly\") " +
                        "VALUES ({0}, 'pc', '2026-01-01 00:00:00', '2099-01-01 00:00:00', 0, '11111111-1111-1111-1111-111111111111', {1}, {2}, 0)",
                        hash, kind, kind == 1 ? "laptop" : "");
                }
            }

            await using var after = new OadmDbContext(options);
            await after.Database.MigrateAsync();
            Assert.Equal(["h-session"], await after.AuthTokens.AsNoTracking().Select(t => t.TokenHash).ToListAsync());
            var columns = await after.Database.SqlQueryRaw<string>("SELECT \"name\" AS \"Value\" FROM pragma_table_info('AuthTokens')").ToListAsync();
            Assert.DoesNotContain("Kind", columns);
            Assert.DoesNotContain("ReadOnly", columns);
            Assert.Empty(await after.AccessKeys.ToListAsync());

            // A key names a role; the role cannot go while the key exists.
            after.AccessKeys.Add(new AccessKeyEntity
            {
                Id = Guid.NewGuid(), TokenHash = "k", Name = "bot", NormalizedName = "BOT", RoleId = BuiltInRoles.ViewerId, AllDevices = true,
                CreatedBy = "admin", CreatedUtc = DateTime.UtcNow, ExpiresUtc = DateTime.UtcNow.AddDays(90),
            });
            await after.SaveChangesAsync();
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(async () =>
                await after.Database.ExecuteSqlRawAsync("DELETE FROM \"Roles\" WHERE \"Kind\" = 'Viewer'"));
        }
        finally
        {
            TestDatabase.DeleteDirectory(dir);
        }
    }
}
