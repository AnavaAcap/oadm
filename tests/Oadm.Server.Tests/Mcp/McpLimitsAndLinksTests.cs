using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Auth;
using Oadm.Server.Mcp;
using Oadm.Server.Tests.Support;
using Oadm.Server.Tools;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Mcp;

/// <summary>
/// The limits per user and for the whole port, what counts as a change, the upload link's address and name, "Use AI agents" on the
/// file routes and the skill, the held handles per user, the cursors bound to their tool, and the names of access keys.
/// </summary>
public sealed class McpLimitsAndLinksTests
{
    private static async Task<string> SecondUserKeyAsync(McpTestServer server, string name = "second") =>
        await server.CreateKeyAsync(await server.Host.InvokerForUserAsync(name, Oadm.Sdk.Plugins.UserRole.Admin), name: "other");

    [Fact]
    public async Task EveryToolThatIsNoPlainReadCountsAsAChangeAndAUsersKeysShareOneBudget()
    {
        await using var server = await McpTestServer.StartAsync();
        var key = await server.CreateKeyAsync();
        var sameUser = await server.CreateKeyAsync(name: "again");
        var otherUser = await SecondUserKeyAsync(server);
        static JsonObject Arguments() => new() { ["handle"] = "none" };

        // A tag run is no task run: it counted for nothing before; every run of any tool counts now (the handle does not exist: still counted).
        for (var i = 0; i < McpAccess.ChangesPerMinute; i++)
        {
            Assert.Equal(200, (await server.PostAsync(key, McpTestServer.Request("tools/call", new JsonObject { ["name"] = "oadm_tags_assign_run", ["arguments"] = Arguments() }))).Status);
        }

        var refused = await server.PostAsync(key, McpTestServer.Request("tools/call", new JsonObject { ["name"] = "oadm_tags_assign_run", ["arguments"] = Arguments() }));
        Assert.Equal(429, refused.Status);
        Assert.Equal("60", refused.RetryAfter);
        Assert.Equal("Too many changes with this access key. Wait a minute.", refused.ErrorMessage);

        // The user's other key shares the budget, another user and plain reads do not.
        var shared = await server.PostAsync(sameUser, McpTestServer.Request("tools/call", new JsonObject { ["name"] = "oadm_devices_remove_run", ["arguments"] = Arguments() }));
        Assert.Equal(429, shared.Status);
        Assert.Equal(200, (await server.PostAsync(sameUser, McpTestServer.Request("tools/call", new JsonObject { ["name"] = "oadm_tags_list", ["arguments"] = new JsonObject() }))).Status);
        Assert.Equal(200, (await server.PostAsync(otherUser, McpTestServer.Request("tools/call", new JsonObject { ["name"] = "oadm_tags_assign_run", ["arguments"] = Arguments() }))).Status);
    }

    [Fact]
    public async Task AUsersKeysShareTheirCallsASecondAndThePortHasOneCapForEveryone()
    {
        await using var server = await McpTestServer.StartAsync();
        var key = await server.CreateKeyAsync();
        var sameUser = await server.CreateKeyAsync(name: "again");
        var otherUser = await SecondUserKeyAsync(server);

        // 20 calls a second for the user: the two keys together (the fake clock stands still within the second only when the test is fast, so count by the limiter's refusal).
        var statuses = new List<int>();
        for (var i = 0; i < McpAccess.CallsPerSecond; i++)
        {
            statuses.Add((await server.PostAsync(i % 2 == 0 ? key : sameUser, McpTestServer.Request("tools/list"))).Status);
        }

        Assert.DoesNotContain(statuses, s => s != 200 && s != 429); // slow machines may start a new second
        Assert.Equal(200, (await server.PostAsync(otherUser, McpTestServer.Request("tools/list"))).Status);

        // The port: more places than one user has are taken by many users; the next call of anyone is refused.
        var access = server.Host.Get<McpAccess>();
        var caller = (await access.AuthenticateAsync(otherUser, "local", CancellationToken.None))!;
        var held = new List<IDisposable>();
        for (var i = 0; i < McpAccess.CallsInPort; i++)
        {
            var someone = caller with { User = caller.User with { UserId = Guid.NewGuid() }, BindingKey = ToolHandles.BindingKeyOf(Guid.NewGuid(), "k" + i) };
            held.Add(access.TryStartCall(someone)!);
        }

        Assert.All(held, Assert.NotNull);
        Assert.Null(access.TryStartCall(caller with { User = caller.User with { UserId = Guid.NewGuid() }, BindingKey = ToolHandles.BindingKeyOf(Guid.NewGuid(), "late") }));
        held[0].Dispose();
        using var free = access.TryStartCall(caller with { User = caller.User with { UserId = Guid.NewGuid() }, BindingKey = ToolHandles.BindingKeyOf(Guid.NewGuid(), "after") });
        Assert.NotNull(free);
        foreach (var place in held)
        {
            place.Dispose();
        }
    }

    [Fact]
    public async Task ACallIsCountedBeforeTheRoleAndTheDeviceViewAreRead()
    {
        await using var host = await TestServerHost.StartAsync();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var tokens = new AuthTokenStore(host.Get<Microsoft.EntityFrameworkCore.IDbContextFactory<Oadm.Core.Persistence.OadmDbContext>>(), time);
        var admin = (await host.Get<UserStore>().FindByNameAsync(TestServerHost.AdminUserName, CancellationToken.None))!;
        var (key, _) = await tokens.CreateAccessKeyAsync(admin.Id, admin.UserName, "laptop", 30, readOnly: false, "test", CancellationToken.None);
        using var access = new McpAccess(tokens, host.Get<Oadm.Core.Auth.RoleStore>(), host.Get<Oadm.Core.DeviceGroups.DeviceViewFactory>(), host.Get<AccessEpochs>(), host.Get<AuditLog>(), time);
        for (var i = 0; i < McpAccess.CallsPerSecond; i++)
        {
            Assert.NotNull((await access.SignInAsync(key, "10.9.0.1", CancellationToken.None)).Caller);
        }

        var over = await access.SignInAsync(key, "10.9.0.1", CancellationToken.None);
        Assert.True(over.Throttled);
        Assert.Null(over.Caller); // refused with the key only: no role, no device view
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.NotNull((await access.SignInAsync(key, "10.9.0.1", CancellationToken.None)).Caller);
    }

    [Fact]
    public async Task TheUploadLinkUsesTheServersOwnNameAndPortNeverTheHostHeaderAndTheNameIsSafeInAShell()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var server = await McpTestServer.StartAsync(accessClock: clock);
        var key = await server.CreateKeyAsync();
        var link = await Link(server, key, "firmware (2).bin", 3, new Dictionary<string, string?> { ["Host"] = "attacker.example:1" });
        Assert.StartsWith("https://localhost:5082/files/", link["url"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal($"curl --cacert oadm-ca.pem -H \"Authorization: Bearer $OADM_KEY\" -T \"firmware (2).bin\" {link["url"]!.GetValue<string>()}", link["command"]!.GetValue<string>());

        foreach (var bad in new[] { "a\"b.bin", "a$(x).bin", "a`x`.bin", "a;b", "a'b", "../a.bin", "a/b", "a\\b", "..", "a&b", "a!b" })
        {
            clock.Advance(TimeSpan.FromMinutes(2)); // a link is a change: a minute apart
            var reply = await server.PostAsync(key, McpTestServer.Request("tools/call", new JsonObject { ["name"] = "oadm_files_upload_link", ["arguments"] = new JsonObject { ["name"] = bad, ["size"] = 3 } }));
            var result = reply.Body!["result"]!;
            Assert.True(result["isError"]!.GetValue<bool>(), bad);
            var text = result["content"]![0]!["text"]!.GetValue<string>();
            Assert.True(text.Contains("letters, digits, spaces", StringComparison.Ordinal), bad + " -> " + text);
        }
    }

    [Fact]
    public void TheLinkNamesTheFirstDnsNameBeforeAnAddress()
    {
        Assert.Equal("https://oadm.example:5082/files/", McpEndpoint.FilesUrlOf(["10.0.0.17", "oadm.example"], 5082));
        Assert.Equal("https://10.0.0.17:5090/files/", McpEndpoint.FilesUrlOf(["10.0.0.17"], 5090));
        Assert.Equal("https://[fd00::1]:5082/files/", McpEndpoint.FilesUrlOf(["fd00::1"], 5082));
        Assert.Null(McpEndpoint.FilesUrlOf([], 5082));
        Assert.Null(McpEndpoint.FilesUrlOf(["oadm.example"], null));
    }

    [Fact]
    public async Task AKeyWhoseRoleLostUseAiAgentsNeitherUploadsNorFetchesAFileNorReadsTheSkill()
    {
        await using var server = await McpTestServer.StartAsync();
        var roles = new Proto.RoleService.RoleServiceClient(server.Host.Invoker);
        var saved = await roles.SaveRolesAsync(new Proto.SaveRolesRequest { Roles = { new Proto.RoleChange { Name = "Agents", Permissions = { Oadm.Core.Auth.HostPermissions.McpUse } } } });
        var role = saved.Roles.Single(r => r.Name == "Agents");
        var key = await server.CreateKeyAsync(await server.Host.InvokerForRoleAsync("agent-user", Guid.Parse(role.Id)));
        var link = await Link(server, key, "a.bin", 3);
        Assert.NotEmpty(await ResourceUrisAsync(server, key));

        await roles.SaveRolesAsync(new Proto.SaveRolesRequest { Roles = { new Proto.RoleChange { Id = role.Id, Name = "Agents" } } });
        using var client = server.Http();
        using var put = new HttpRequestMessage(HttpMethod.Put, new Uri(link["url"]!.GetValue<string>()).PathAndQuery) { Content = new ByteArrayContent([1, 2, 3]) };
        put.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        using var denied = await client.SendAsync(put);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Contains("Use AI agents", await denied.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var get = new HttpRequestMessage(HttpMethod.Get, McpEndpoint.FilesPath + "anything");
        get.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await client.SendAsync(get)).StatusCode);

        Assert.Empty(await ResourceUrisAsync(server, key));
        var read = await server.PostAsync(key, McpTestServer.Request("resources/read", new JsonObject { ["uri"] = McpSkill.UriOf(McpSkill.Files.Keys.First()) }),
            new Dictionary<string, string?> { ["Mcp-Name"] = McpSkill.UriOf(McpSkill.Files.Keys.First()) });
        Assert.NotNull(read.Body?["error"]);
        Assert.Contains("Use AI agents", read.Body!["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void HandlesAreCappedPerUserOverAllTheirKeysAndByHeldSizeTheOldestFirst()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var handles = new ToolHandles(time);
        var user = Guid.NewGuid();
        var first = handles.AddRun(ToolHandles.BindingKeyOf(user, "k0"), new PlannedRun("p", ["a"], null, "t"));
        var tokens = new List<string>();
        for (var key = 1; key <= 4; key++)
        {
            for (var i = 0; i < ToolHandles.MaxPerCaller; i++)
            {
                tokens.Add(handles.AddRun(ToolHandles.BindingKeyOf(user, "k" + key), new PlannedRun("p", ["a"], null, "t")));
            }
        }

        // 1 + 80 made, 60 kept for the user, the oldest gone; another user is not touched.
        Assert.Null(handles.TakeRun(ToolHandles.BindingKeyOf(user, "k0"), first));
        Assert.Null(handles.TakeRun(ToolHandles.BindingKeyOf(user, "k1"), tokens[0]));
        Assert.NotNull(handles.TakeRun(ToolHandles.BindingKeyOf(user, "k4"), tokens[^1]));
        var other = Guid.NewGuid();
        var theirs = handles.AddRun(ToolHandles.BindingKeyOf(other, "k"), new PlannedRun("p", ["a"], null, "t"));
        Assert.NotNull(handles.TakeRun(ToolHandles.BindingKeyOf(other, "k"), theirs));

        // Size: plans of 100,000 device ids (about 4 MB each) held by one user stay within 16 MB.
        var ids = Enumerable.Range(0, 100_000).Select(i => Guid.NewGuid().ToString()).ToList();
        var big = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            big.Add(handles.AddRun(ToolHandles.BindingKeyOf(user, "big" + i), new PlannedRun("p", ids, null, "t")));
        }

        Assert.Null(handles.TakeRun(ToolHandles.BindingKeyOf(user, "big0"), big[0])); // the oldest went to make room
        Assert.NotNull(handles.TakeRun(ToolHandles.BindingKeyOf(user, "big5"), big[5]));
    }

    [Fact]
    public async Task ACursorOfOneListIsRefusedByAnotherListWithTheSameArguments()
    {
        await using var server = await McpTestServer.StartAsync();
        var key = await server.CreateKeyAsync();
        foreach (var name in new[] { "Alpha", "Beta", "Gamma" })
        {
            await server.Host.Tags.CreateAsync(new Proto.CreateTagRequest { Name = name });
        }

        var page = await server.ToolAsync(key, "oadm_tags_list", new JsonObject { ["limit"] = 1 });
        var cursor = page["next_cursor"]!.GetValue<string>();
        var again = await server.ToolAsync(key, "oadm_tags_list", new JsonObject { ["limit"] = 1, ["cursor"] = cursor });
        Assert.NotNull(again["rows"] ?? again["tags"]);

        // The same arguments and cursor in a different list tool: refused, and for the logs never parsed.
        foreach (var tool in new[] { "oadm_sites_list", "oadm_logs_audit", "oadm_logs_search", "oadm_events_log", "oadm_rules_list" })
        {
            var result = await server.CallToolAsync(key, tool, new JsonObject { ["limit"] = 1, ["cursor"] = cursor });
            Assert.True(result["isError"]!.GetValue<bool>(), tool);
            Assert.Contains(ToolCursors.NotValid, result["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AServerLogCursorWithTextInsteadOfANumberIsNeverParsedAsOne()
    {
        await using var server = await McpTestServer.StartAsync();
        var cursors = server.Host.Get<ToolCursors>();
        var key = await server.CreateKeyAsync();
        var caller = (await server.Host.Get<McpAccess>().AuthenticateAsync(key, "local", CancellationToken.None))!;
        var call = new ToolCall(caller, System.Text.Json.JsonDocument.Parse("{}").RootElement, null!, CancellationToken.None) { Tool = "oadm_logs_search" };
        var text = cursors.Make(caller, ToolCursors.QueryOf(call), "2026-01-01T00:00:00.000Z", "not a number");
        var reply = await server.CallToolAsync(key, "oadm_logs_search", new JsonObject { ["cursor"] = text });
        Assert.False(reply["isError"]?.GetValue<bool>() ?? false, reply.ToJsonString()); // read as "no lines shown yet", never an exception
    }

    [Theory]
    [InlineData("a$(x)")]
    [InlineData("name\"quoted")]
    [InlineData("under`tick")]
    [InlineData("semi;colon")]
    [InlineData("new/line")]
    public async Task AnAccessKeyNameAllowsOnlyLettersDigitsSpaceDashUnderscoreAndDot(string name)
    {
        await using var server = await McpTestServer.StartAsync();
        var refused = await Assert.ThrowsAsync<Grpc.Core.RpcException>(async () => await server.CreateKeyAsync(name: name));
        Assert.Equal(Grpc.Core.StatusCode.InvalidArgument, refused.StatusCode);
        Assert.Equal(AccessKeyTexts.NameCharacters, refused.Status.Detail);
        await server.CreateKeyAsync(name: "Laptop 2.0_a-b"); // fine
        Assert.Equal("Old name_", AuthTokenStore.CleanAccessKeyName("Old name$"));
    }

    private static async Task<JsonObject> Link(McpTestServer server, string key, string name, int size, IReadOnlyDictionary<string, string?>? headers = null)
    {
        var reply = await server.PostAsync(key, McpTestServer.Request("tools/call", new JsonObject { ["name"] = "oadm_files_upload_link", ["arguments"] = new JsonObject { ["name"] = name, ["size"] = size } }), headers);
        Assert.Equal(200, reply.Status);
        return (JsonObject)reply.Body!["result"]!["structuredContent"]!;
    }

    private static async Task<List<string>> ResourceUrisAsync(McpTestServer server, string key)
    {
        var listed = await server.PostAsync(key, McpTestServer.Request("resources/list"));
        return [.. listed.Body!["result"]!["resources"]!.AsArray().Select(r => r!["uri"]!.GetValue<string>())];
    }
}
