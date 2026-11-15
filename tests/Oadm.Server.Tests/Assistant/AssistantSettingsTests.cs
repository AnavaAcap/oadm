using System.Text.Json;

using Grpc.Core;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;

using Oadm.Contracts.Assistant;
using Oadm.Core.Auth;
using Oadm.Server.Assistant;
using Oadm.Server.Tools;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Assistant;

/// <summary>
/// The Settings card AI, section Assistant (docs/spec/assistant.md "Settings"): validation below the fields (trailer
/// oadm-field), the key stored encrypted and kept, the audit entry, Test, the daily limit per server and per user, and the
/// docs search the assistant answers OADM questions with.
/// </summary>
public sealed class AssistantSettingsTests
{
    public static TheoryData<string, Action<Proto.AssistantSettings>, string> Refusals => new()
    {
        { "provider", s => s.Provider = "gemini", AssistantTexts.ChooseProvider },
        { "address", s => { s.Provider = AssistantTexts.Ollama; s.Address = string.Empty; }, AssistantTexts.EnterAddress },
        { "address", s => { s.Provider = AssistantTexts.OpenAiCompatible; s.Address = "ftp://10.0.0.5"; }, AssistantTexts.AddressNotValid },
        { "api_key", s => s.ApiKey = new string('k', 501), AssistantTexts.ApiKeyTooLong },
        { "daily_limit", s => s.DailyLimit = 10, AssistantTexts.DailyLimitRange },
        { "user_daily_limit", s => s.UserDailyLimit = 5, AssistantTexts.UserDailyLimitRange },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task AWrongFieldIsRefusedWithItsName(string field, Action<Proto.AssistantSettings> change, string message)
    {
        await using var server = await AssistantTestServer.StartAsync(turnOn: false);
        var settings = AssistantTestServer.Settings();
        change(settings);

        var refused = await Assert.ThrowsAsync<RpcException>(async () => await server.Service().SetSettingsAsync(settings));

        Assert.Equal(StatusCode.InvalidArgument, refused.StatusCode);
        Assert.Equal(message, refused.Status.Detail);
        Assert.Equal(field, refused.Trailers.GetValue(AssistantTexts.FieldTrailer));
    }

    [Fact]
    public async Task TheKeyIsStoredEncryptedKeptWhenEmptyNeverSentBackAndEveryChangeIsAudited()
    {
        await using var server = await AssistantTestServer.StartAsync();
        var state = await server.Service().GetSettingsAsync(new Proto.Empty());
        Assert.True(state.HasApiKey);
        Assert.Empty(state.Settings.ApiKey);
        Assert.Equal(AssistantTexts.On, state.Status);

        // Saved again without a key: the saved one stays (Turn on with Anthropic needs one).
        var again = AssistantTestServer.Settings();
        again.ApiKey = string.Empty;
        again.SendPictures = true;
        var saved = await server.SetAsync(again);
        Assert.True(saved.HasApiKey);
        server.Model.Say("Hi.");
        await server.ChatAsync("Hi");
        Assert.Equal(AssistantTestServer.ApiKey, server.Providers.Connections[^1].ApiKey);

        // Another provider without a key: the old key is not taken along.
        var openAi = AssistantTestServer.Settings(AssistantTexts.OpenAi);
        openAi.ApiKey = string.Empty;
        var refused = await Assert.ThrowsAsync<RpcException>(async () => await server.Service().SetSettingsAsync(openAi));
        Assert.Equal("api_key", refused.Trailers.GetValue(AssistantTexts.FieldTrailer));

        var entries = (await server.Host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries.Where(e => e.Action == AuditActions.AssistantChanged).ToList();
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Detail.Contains("Turned on", StringComparison.Ordinal) && e.Detail.Contains("API key changed", StringComparison.Ordinal));
        Assert.Contains(entries, e => e.Detail == "Send pictures to the model on");

        // Turned off: every chat ends.
        await server.SetAsync(AssistantTestServer.Settings(enabled: false));
        Assert.Equal(0, server.Host.Get<AssistantConversations>().Count);
    }

    [Fact]
    public async Task TestReadsTheModelListWithoutAModelAndNeverSaves()
    {
        await using var server = await AssistantTestServer.StartAsync(turnOn: false);
        var ok = await server.Service().TestAsync(AssistantTestServer.Settings());
        Assert.True(ok.Ok, ok.Message);
        Assert.Equal(AssistantTexts.ProviderAnswered, ok.Message);
        Assert.Equal(AssistantTestServer.ApiKey, server.Providers.Connections[^1].ApiKey);
        Assert.Empty(server.Model.Requests); // no chat call: nothing needs a model

        // An OpenAI-compatible server without a list answered too.
        server.Providers.Read = _ => null;
        Assert.True((await server.Service().TestAsync(AssistantTestServer.Settings(AssistantTexts.OpenAiCompatible))).Ok);

        server.Providers.Read = _ => throw new HttpRequestException("nope " + AssistantTestServer.ApiKey, null, System.Net.HttpStatusCode.Unauthorized);
        var refused = await server.Service().TestAsync(AssistantTestServer.Settings());
        Assert.False(refused.Ok);
        Assert.Equal(AssistantProviderErrors.KeyRefused, refused.Message);

        server.Providers.Read = _ => throw new HttpRequestException("no route", new System.Net.Sockets.SocketException());
        Assert.Equal(AssistantProviderErrors.Unreachable, (await server.Service().TestAsync(AssistantTestServer.Settings(AssistantTexts.Ollama))).Message);

        Assert.False((await server.Service().GetSettingsAsync(new Proto.Empty())).Settings.Enabled);
    }

    [Fact]
    public void TheDailyLimitCountsPerServerAndPerUserAndStartsAgainAtMidnight()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 15, 22, 0, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var usage = new AssistantUsage(time);
        var anna = Guid.NewGuid();
        var ben = Guid.NewGuid();

        usage.Add(anna, 600);
        Assert.False(usage.Reached(anna, serverLimit: 1_000, userLimit: 0));
        Assert.True(usage.Reached(anna, serverLimit: 1_000, userLimit: 500)); // the user's own limit
        Assert.False(usage.Reached(ben, serverLimit: 1_000, userLimit: 500));
        usage.Add(ben, 400);
        Assert.True(usage.Reached(ben, serverLimit: 1_000, userLimit: 0)); // the server's limit counts everyone
        Assert.Equal(1_000, usage.UsedToday());
        Assert.Equal(400, usage.UsedToday(ben));

        time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(59)); // 23:59
        Assert.True(usage.Reached(ben, 1_000, 0));
        time.Advance(TimeSpan.FromMinutes(1)); // midnight
        Assert.False(usage.Reached(ben, 1_000, 0));
        Assert.Equal(0, usage.UsedToday());
        Assert.Equal(0, usage.UsedToday(anna));
    }

    [Fact]
    public void TheDocsSearchFindsAKnownPage()
    {
        var index = DocsIndex.Shipped;
        Assert.Contains(index.Pages, p => p.Url == "https://oadm.app/devices/add/");
        Assert.DoesNotContain(index.Pages, p => p.Url.Contains("/dev/", StringComparison.Ordinal) || p.Url.Contains("/api/", StringComparison.Ordinal));
        Assert.All(index.Pages, p => Assert.DoesNotContain("import {", p.Text, StringComparison.Ordinal));

        var hits = index.Search("How do I add devices from a network range?");
        Assert.InRange(hits.Count, 1, DocsIndex.MaxHits);
        Assert.Equal("https://oadm.app/devices/add/", hits[0].Url);
        Assert.Equal("Add devices", hits[0].Title);
        Assert.Contains("range", hits[0].Passage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(index.Search("the a of"));
    }

    [Fact]
    public async Task TheDocsSearchToolAnswersEveryCallerWithinTheLimit()
    {
        await using var host = await Support.TestServerHost.StartAsync();
        var registry = host.Get<ToolRegistry>();
        var admin = (await host.Get<UserStore>().FindByNameAsync(Support.TestServerHost.AdminUserName, CancellationToken.None))!;
        var caller = new ToolCaller(new AuthenticatedCaller(admin.Id, admin.UserName, BuiltInRoles.ViewerId, PermissionSet.None, "127.0.0.1", "x", "binding"), "binding");
        Assert.Contains(registry.For(caller), t => t.Name == DocsTools.Name);

        var result = await registry.CallAsync(caller, DocsTools.Name, JsonDocument.Parse("""{"query": "upgrade AXIS OS firmware"}""").RootElement, CancellationToken.None);
        Assert.False(result.IsError, result.Error);
        Assert.InRange(ToolAnswers.SizeOf(result.Answer!), 1, ToolAnswers.MaxBytes);
        Assert.StartsWith("https://oadm.app/", result.Answer!["hits"]![0]!["url"]!.GetValue<string>(), StringComparison.Ordinal);

        var nothing = await registry.CallAsync(caller, DocsTools.Name, JsonDocument.Parse("""{"query": "zzqx"}""").RootElement, CancellationToken.None);
        Assert.Equal(DocsTools.NothingFound, nothing.Answer!["note"]!.GetValue<string>());

        // Over MCP nothing is selected: the selection answers empty.
        var selection = await registry.CallAsync(caller with { User = caller.User with { RoleId = admin.RoleId } }, SelectionTools.Name, null, CancellationToken.None);
        Assert.Equal(0, selection.Answer?["total"]?.GetValue<int>() ?? -1);
    }

    [Fact]
    public void EveryProviderMakesAChatClientWithoutCallingIt()
    {
        using var providers = new AssistantProviders(TimeProvider.System);
        foreach (var provider in AssistantTexts.Providers)
        {
            using var client = providers.Create(new AssistantConnection(provider, "http://127.0.0.1:9/v1", "test-model", "sk-test"));
            Assert.NotNull(client);
        }

        Assert.Equal("http://127.0.0.1:9/v1/", AssistantProviders.BaseOf("http://127.0.0.1:9/v1").ToString());
        Assert.Throws<ArgumentException>(() => providers.Create(new AssistantConnection("gemini", null, "m", null)));
    }

    [Fact]
    public void TheOfferedToolsAreReadsAndPreviewsOnly()
    {
        var read = Tool("oadm_x_list");
        Assert.True(AssistantTools.IsOffered(read));
        Assert.True(AssistantTools.IsOffered(Tool("oadm_x_preview")));
        Assert.False(AssistantTools.IsOffered(Tool("oadm_x_run")));
        Assert.False(AssistantTools.IsOffered(read with { ReadsOnly = false }));
        Assert.False(AssistantTools.IsOffered(read with { StartsTasks = true }));
        Assert.False(AssistantTools.IsOffered(Tool("oadm_files_upload_link")));
        Assert.Equal("oadm_tags_assign_run", AssistantTools.RunOf("oadm_tags_assign_preview"));
        Assert.Equal("oadm_x", AssistantTools.Declare(read).Name[..6]);
        Assert.IsAssignableFrom<AIFunctionDeclaration>(AssistantTools.Declare(read));
    }

    private static OadmTool Tool(string name) =>
        new(name, "T", "D.", ToolSchema.Object(null, []), ToolSchema.OutObject(null, []), [], _ => Task.FromResult(new System.Text.Json.Nodes.JsonObject()));
}
