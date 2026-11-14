using System.Text.Json.Nodes;

using Grpc.Core;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

using Oadm.Contracts.Mcp;
using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Core.Persistence;
using Oadm.Server.Mcp;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Mcp;

/// <summary>
/// The MCP server's transport and keys (docs/spec/mcp.md "Rules that never break", "Transport and certificate", "Access keys"):
/// off by default, the certificate from PKI, the origin and header checks, the body limit, the key checks and the per-key limit.
/// </summary>
public sealed class McpServerTests
{
    [Fact]
    public async Task ItIsOffByDefaultAndItsPortStaysClosed()
    {
        await using var server = await McpTestServer.StartAsync(turnOn: false);
        Assert.Null(server.Listener.App);
        var state = await server.McpService().GetAsync(new Proto.Empty());
        Assert.False(state.Settings.Enabled);
        Assert.False(state.Running);
        Assert.Equal(McpSettings.DefaultPort, state.Settings.Port);
        Assert.Equal(McpTexts.Off, state.Status);
        Assert.True(state.PkiAvailable);

        // While it is off PKI is never asked: its first load is the first Turn on, which gives the policy AI agents a certificate.
        Assert.Equal(0, server.Certificates.Loads);
        Assert.Equal(string.Empty, state.Certificate);
        await server.SetAsync(enabled: true);
        Assert.True(server.Certificates.Loads > 0);
        Assert.NotEqual(string.Empty, (await server.McpService().GetAsync(new Proto.Empty())).Certificate);
    }

    [Fact]
    public async Task AnswersAreNeverCompressedAlsoWhenTheAgentAsksForGzip()
    {
        await using var server = await McpTestServer.StartAsync();
        var key = await server.CreateKeyAsync();
        var reply = await server.PostAsync(key, McpTestServer.Request("tools/list"), new Dictionary<string, string?> { ["Accept-Encoding"] = "gzip, br" });
        Assert.Equal(200, reply.Status);
        Assert.DoesNotContain(reply.ContentEncoding, e => e is "gzip" or "br");
        Assert.NotNull(reply.Body?["result"]?["tools"]);
    }

    [Fact]
    public async Task EachKeyHasAtMostFourCallsWaitingForAnAnswerThen429()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var held = new McpTool("oadm_test_hold", "Hold", "Waits for the test.", McpSchema.Object(null, []), McpSchema.OutObject(null, []), [], async _ =>
        {
            Interlocked.Increment(ref entered);
            await gate.Task;
            return [];
        });
        await using var server = await McpTestServer.StartAsync(configureServices: services =>
        {
            services.RemoveAll<McpTools>();
            services.AddSingleton(sp => new McpTools([held], sp.GetRequiredService<Oadm.Server.Auth.InProcessCalls>(), sp.GetRequiredService<RoleStore>()));
        });
        var key = await server.CreateKeyAsync();
        var other = await server.CreateKeyAsync(name: "other");
        var calls = Enumerable.Range(0, McpAccess.CallsAtOnce).Select(_ => server.CallToolAsync(key, "oadm_test_hold")).ToList();
        await Oadm.Sdk.Testing.Eventually.TrueAsync(() => Volatile.Read(ref entered) == McpAccess.CallsAtOnce, "every allowed call waits in the tool");

        var refused = await server.PostAsync(key, McpTestServer.Request("tools/call", new JsonObject { ["name"] = "oadm_test_hold", ["arguments"] = new JsonObject() }));
        Assert.Equal(429, refused.Status);
        Assert.Equal("1", refused.RetryAfter);
        Assert.Equal(McpEndpoint.TooManyAtOnce, refused.ErrorMessage);
        Assert.Equal(200, (await server.PostAsync(other, McpTestServer.Request("tools/list"))).Status); // another key has its own

        gate.SetResult();
        foreach (var call in calls)
        {
            Assert.False((await call)["isError"]?.GetValue<bool>() ?? false);
        }

        Assert.Equal(200, (await server.PostAsync(key, McpTestServer.Request("tools/list"))).Status); // the places are free again
    }

    [Fact]
    public async Task WithoutPKIItCannotBeTurnedOnAndTheCardSaysWhy()
    {
        await using var server = await McpTestServer.StartAsync(turnOn: false, withPki: false);
        var refused = await Assert.ThrowsAsync<RpcException>(async () => await server.SetAsync(enabled: true));
        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
        Assert.Equal(McpTexts.NoPki, refused.Status.Detail);
        Assert.False((await server.McpService().GetAsync(new Proto.Empty())).PkiAvailable);
        Assert.Null(server.Listener.App);
    }

    [Fact]
    public async Task TurnedOnItListensWithTheCertificatePKIPicksAndClosesWhenTheCertificateGoes()
    {
        await using var server = await McpTestServer.StartAsync();
        var state = await server.McpService().GetAsync(new Proto.Empty());
        Assert.True(state.Running);
        Assert.Equal(McpTexts.Running(McpSettings.DefaultPort), state.Status);
        Assert.Contains("localhost", state.CertificateNames);
        Assert.True(state.HasCaCertificate);
        var ca = await server.McpService().GetCaCertificateAsync(new Proto.Empty());
        Assert.Equal("oadm-ca.pem", ca.FileName);
        Assert.DoesNotContain("PRIVATE KEY", ca.Pem, StringComparison.Ordinal); // only the issuers' certificates, never a key
        using (var pem = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(ca.Pem))
        {
            Assert.Equal(server.Certificates.Ca.Thumbprint, pem.Thumbprint);
        }

        server.Certificates.Problem = "The MCP server has no certificate. Choose one in its policy.";
        server.Certificates.RaiseChanged();
        await TestHelpers.WaitUntilAsync(() => Task.FromResult(server.Listener.App is null), "the port closes");
        state = await server.McpService().GetAsync(new Proto.Empty());
        Assert.False(state.Running);
        Assert.Equal(server.Certificates.Problem, state.Status);
    }

    [Fact]
    public async Task TheWindowsServiceKeepsAFirewallRuleForThePortOnlyWhileItListens()
    {
        var firewall = new Oadm.Tests.Shared.RecordingFirewall();
        await using var server = await McpTestServer.StartAsync(firewall: firewall);
        Assert.Equal(["close OADM Server (AI agents, TCP 5082)", "open OADM Server (AI agents, TCP 5082)"], firewall.CallList); // a rule a crash left goes first
        await server.SetAsync(enabled: true, port: 5090);
        await server.SetAsync(enabled: false, port: 5090);
        Assert.Equal(
            [
                "close OADM Server (AI agents, TCP 5082)", "open OADM Server (AI agents, TCP 5082)", "close OADM Server (AI agents, TCP 5082)",
                "open OADM Server (AI agents, TCP 5090)", "close OADM Server (AI agents, TCP 5090)",
            ],
            firewall.CallList);
    }

    [Fact]
    public async Task OnlyAdministratorsChangeItAndEveryChangeIsAudited()
    {
        await using var server = await McpTestServer.StartAsync(turnOn: false);
        var operatorCalls = await server.Host.InvokerForUserAsync("op", Oadm.Sdk.Plugins.UserRole.Operator);
        var refused = await Assert.ThrowsAsync<RpcException>(async () => await server.McpService(operatorCalls).SetAsync(new Proto.McpSettings { Enabled = true, Port = 5082 }));
        Assert.Equal(StatusCode.PermissionDenied, refused.StatusCode);
        Assert.False((await server.McpService(operatorCalls).GetAsync(new Proto.Empty())).Settings.Enabled); // every role reads it

        var badPort = await Assert.ThrowsAsync<RpcException>(async () => await server.SetAsync(enabled: true, port: 80));
        Assert.Equal(McpTexts.PortRange, badPort.Status.Detail);

        await server.SetAsync(enabled: true, port: 5090);
        var audit = await server.Host.Audit.ListAsync(new Proto.ListAuditRequest());
        var entry = audit.Entries.First(e => e.Action == AuditActions.AiAgentsChanged);
        Assert.Equal("Turned on, Port 5082 -> 5090", entry.Detail);
    }

    [Fact]
    public async Task AMissingUnknownOrEndedKeyGets401WithTheTextAndIsAuditedOnceAMinute()
    {
        await using var server = await McpTestServer.StartAsync();
        var none = await server.PostAsync(null, McpTestServer.Request("tools/list"));
        Assert.Equal(401, none.Status);
        Assert.Equal(AccessKeyTexts.NotValid, none.ErrorMessage);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(401, (await server.PostAsync("oadm_unknownkey", McpTestServer.Request("tools/list"))).Status);
        }

        var entries = (await server.Host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries.Where(e => e.Action == AuditActions.AccessKeyRefused).ToList();
        var unknown = Assert.Single(entries, e => e.Target == "Unknown access key");
        Assert.Equal("3 refused calls", unknown.Detail);
        Assert.Single(entries, e => e.Target == "No access key");

        // Like logins: after 50 refused keys from an address in 5 minutes it waits, before any lookup.
        for (var i = 4; i < McpAccess.MaxRefusedPerAddress; i++)
        {
            Assert.Equal(401, (await server.PostAsync("oadm_unknownkey", McpTestServer.Request("tools/list"))).Status);
        }

        var limited = await server.PostAsync("oadm_unknownkey", McpTestServer.Request("tools/list"));
        Assert.Equal(429, limited.Status);
        Assert.Equal(McpEndpoint.TooManyRefused, limited.ErrorMessage);
    }

    [Fact]
    public async Task AnEndedKeyIsRefusedAndTheAuditLogNamesItsUserAndKeyUntilTheHourlyCleanup()
    {
        await using var host = await TestServerHost.StartAsync();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var tokens = new AuthTokenStore(host.Get<IDbContextFactory<OadmDbContext>>(), time);
        var admin = (await host.Get<UserStore>().FindByNameAsync(TestServerHost.AdminUserName, CancellationToken.None))!;
        var (key, _) = await tokens.CreateAccessKeyAsync(admin.Id, admin.UserName, "laptop", 30, readOnly: false, "test", CancellationToken.None);
        using var access = new McpAccess(tokens, host.Get<RoleStore>(), host.Get<DeviceViewFactory>(), host.Get<AccessEpochs>(), host.Get<AuditLog>(), time);
        Assert.NotNull(await access.AuthenticateAsync(key, "10.9.0.1", CancellationToken.None));

        time.Advance(TimeSpan.FromDays(30) + TimeSpan.FromMinutes(1));
        Assert.Null(await access.AuthenticateAsync(key, "10.9.0.1", CancellationToken.None));
        Assert.Null(await access.AuthenticateAsync(key, "10.9.0.1", CancellationToken.None));
        var entry = Assert.Single((await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries, e => e.Action == AuditActions.AccessKeyRefused);
        Assert.Equal(AuditActions.ViaAccessKey(TestServerHost.AdminUserName, "laptop"), entry.UserName);
        Assert.Equal("laptop", entry.Target);
        Assert.Equal("The key has ended. 2 refused calls", entry.Detail);

        // The hourly cleanup removes the ended key; a later refusal knows it no more.
        await tokens.DeleteExpiredAsync(CancellationToken.None);
        Assert.Empty(await tokens.ListAccessKeysAsync(admin.Id, CancellationToken.None));
        Assert.Null(await tokens.OwnerOfRefusedKeyAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task ASessionTokenIsNoAccessKeyAndAnAccessKeyIsNoSession()
    {
        await using var server = await McpTestServer.StartAsync();
        Assert.Equal(401, (await server.PostAsync(server.Host.AdminToken, McpTestServer.Request("tools/list"))).Status);
        var key = await server.CreateKeyAsync();
        Assert.StartsWith("oadm_", key, StringComparison.Ordinal);
        var refused = await Assert.ThrowsAsync<RpcException>(async () => await new Proto.DeviceService.DeviceServiceClient(server.Host.InvokerFor(key)).ListAsync(new Proto.Empty()));
        Assert.Equal(StatusCode.Unauthenticated, refused.StatusCode);
    }

    [Fact]
    public async Task ABrowserOriginOtherThanTheServerIsRefused()
    {
        await using var server = await McpTestServer.StartAsync();
        var key = await server.CreateKeyAsync();
        var foreign = await server.PostAsync(key, McpTestServer.Request("tools/list"), new Dictionary<string, string?> { ["Origin"] = "https://attacker.example" });
        Assert.Equal(403, foreign.Status);
        Assert.Equal(McpEndpoint.BrowserRefused, foreign.ErrorMessage);
        Assert.Null(foreign.Body?["id"]);
        var own = await server.PostAsync(key, McpTestServer.Request("tools/list"), new Dictionary<string, string?> { ["Origin"] = "https://localhost:5082" });
        Assert.Equal(200, own.Status);

        // A rebound name: the browser sends the page's name as Origin and Host; it is no name of the certificate.
        var rebound = await server.PostAsync(key, McpTestServer.Request("tools/list"),
            new Dictionary<string, string?> { ["Origin"] = "https://attacker.example:5082", ["Host"] = "attacker.example:5082" });
        Assert.Equal(403, rebound.Status);
        var otherPort = await server.PostAsync(key, McpTestServer.Request("tools/list"), new Dictionary<string, string?> { ["Origin"] = "https://localhost:8443" });
        Assert.Equal(403, otherPort.Status);
    }

    [Theory]
    [InlineData("https://oadm.example:5082", "oadm.example:5082", true)]
    [InlineData("https://10.0.0.17:5082", "10.0.0.17:5082", true)]
    [InlineData("https://attacker.example:5082", "attacker.example:5082", false)] // DNS rebinding: Origin and Host agree, the certificate does not name it
    [InlineData("https://oadm.example:443", "oadm.example:5082", false)]
    [InlineData("http://oadm.example:5082", "oadm.example:5082", false)]
    [InlineData("null", "oadm.example:5082", false)]
    public void TheOriginMustBeTheServerUnderANameOfItsCertificate(string origin, string host, bool allowed)
    {
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Request.Scheme = "https";
        http.Request.Host = new Microsoft.AspNetCore.Http.HostString(host);
        http.Request.Headers.Origin = origin;
        Assert.Equal(allowed, McpEndpoint.SameOrigin(http, ["oadm.example", "10.0.0.17"]));

        http.Request.Headers.Origin = string.Empty;
        Assert.True(McpEndpoint.SameOrigin(http, ["oadm.example"])); // no Origin: an agent, not a browser
    }

    [Fact]
    public async Task OnlyPOSTOnMcpIsServed()
    {
        await using var server = await McpTestServer.StartAsync();
        var key = await server.CreateKeyAsync();
        using var http = server.Http();
        using var get = new HttpRequestMessage(HttpMethod.Get, "mcp");
        get.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        Assert.Equal(System.Net.HttpStatusCode.MethodNotAllowed, (await http.SendAsync(get)).StatusCode);
        using var other = new HttpRequestMessage(HttpMethod.Post, "sse");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await http.SendAsync(other)).StatusCode);
    }

    [Fact]
    public async Task ABodyOver1MBIsRefused()
    {
        await using var server = await McpTestServer.StartAsync();
        var key = await server.CreateKeyAsync();
        var body = McpTestServer.Request("tools/call", new JsonObject { ["name"] = "oadm_devices_search", ["arguments"] = new JsonObject { ["filter"] = new JsonObject { ["search"] = new string('x', 1_100_000) } } });
        var reply = await server.PostAsync(key, body);
        Assert.Equal(413, reply.Status);
    }

    [Fact]
    public async Task HeadersThatDoNotMatchTheBodyAndOtherRevisionsAreRefused()
    {
        await using var server = await McpTestServer.StartAsync();
        var key = await server.CreateKeyAsync();
        var call = McpTestServer.Request("tools/call", new JsonObject { ["name"] = "oadm_server_info", ["arguments"] = new JsonObject() });

        var method = await server.PostAsync(key, call, new Dictionary<string, string?> { ["Mcp-Method"] = "tools/list" });
        Assert.Equal(400, method.Status);
        Assert.Equal(-32020, method.ErrorCode);

        var name = await server.PostAsync(key, call, new Dictionary<string, string?> { ["Mcp-Name"] = "oadm_devices_search" });
        Assert.Equal(400, name.Status);
        Assert.Equal(-32020, name.ErrorCode);

        var version = await server.PostAsync(key, call, new Dictionary<string, string?> { ["MCP-Protocol-Version"] = null });
        Assert.True(version.Status == 400 && version.ErrorCode == -32020, server.LastRaw);

        var otherVersion = await server.PostAsync(key, call, new Dictionary<string, string?> { ["MCP-Protocol-Version"] = "2025-11-25" });
        Assert.True(otherVersion.Status == 400, server.LastRaw);

        var missing = await server.PostAsync(key, call, new Dictionary<string, string?> { ["Mcp-Name"] = null });
        Assert.Equal(400, missing.Status);
        Assert.Equal(-32020, missing.ErrorCode);

        // No initialize: the server speaks 2026-07-28 only.
        var initialize = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = "2025-11-25",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "old", ["version"] = "1" },
            },
        };
        var withoutHeader = await server.PostAsync(key, initialize, new Dictionary<string, string?> { ["MCP-Protocol-Version"] = null });
        Assert.Equal(McpEndpoint.HeaderMismatch, withoutHeader.ErrorCode);
        var old = await server.PostAsync(key, initialize, new Dictionary<string, string?> { ["MCP-Protocol-Version"] = "2025-11-25" });
        Assert.True(old.ErrorCode is not null && old.Body?["result"] is null, server.LastRaw);
    }

    [Fact]
    public async Task TheOfficialClientConnectsWithoutASessionAndCallsATool()
    {
        await using var server = await McpTestServer.StartAsync();
        var key = await server.CreateKeyAsync();
        await using var client = await server.ConnectAsync(key);
        var tools = await client.ListToolsAsync();
        Assert.Contains(tools, t => t.Name == "oadm_server_info");
        var result = await client.CallToolAsync("oadm_server_info", new Dictionary<string, object?>());
        Assert.NotEqual(true, result.IsError);
    }

    [Fact]
    public async Task EachKeyMakesAtMost20CallsASecondThen429WithRetryAfter()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var server = await McpTestServer.StartAsync(configureServices: services =>
        {
            services.RemoveAll<McpAccess>();
            services.AddSingleton(sp => new McpAccess(sp.GetRequiredService<AuthTokenStore>(), sp.GetRequiredService<RoleStore>(),
                sp.GetRequiredService<DeviceViewFactory>(), sp.GetRequiredService<AccessEpochs>(), sp.GetRequiredService<AuditLog>(), time));
        });
        var key = await server.CreateKeyAsync();
        var other = await server.CreateKeyAsync(name: "second");
        for (var i = 0; i < McpAccess.CallsPerSecond; i++)
        {
            Assert.Equal(200, (await server.PostAsync(key, McpTestServer.Request("tools/list"))).Status);
        }

        var limited = await server.PostAsync(key, McpTestServer.Request("tools/list"));
        Assert.Equal(429, limited.Status);
        Assert.Equal("1", limited.RetryAfter);
        Assert.Equal(200, (await server.PostAsync(other, McpTestServer.Request("tools/list"))).Status); // per key

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(200, (await server.PostAsync(key, McpTestServer.Request("tools/list"))).Status);
    }
}
