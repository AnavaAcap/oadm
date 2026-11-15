using System.Text.Json;
using System.Text.Json.Nodes;

using Grpc.Core;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

using Oadm.Contracts.Assistant;
using Oadm.Core.Auth;
using Oadm.Core.LiveView;
using Oadm.Core.LiveView.Rtsp;
using Oadm.Core.Vapix;
using Oadm.Server.Assistant;
using Oadm.Server.LiveView;
using Oadm.Server.Tests.Support;
using Oadm.Server.Tools;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Assistant;

/// <summary>
/// The review's rules of the assistant: what a card says Run does, the channel limits, the saved key and its address, the
/// models list, OADM's own failures, the daily estimate, Stop before an answer, the 120 s limit, the idle end and the context.
/// </summary>
public sealed class AssistantLimitsTests
{
    private static readonly JsonElement NoArguments = JsonDocument.Parse("{}").RootElement;

    private static OadmTool Tool(string name, bool destructive = false) =>
        new(name, "Preview", "D.", ToolSchema.Object(null, []), ToolSchema.OutObject(null, []), [], _ => Task.FromResult(new JsonObject())) { Destructive = destructive };

    // ---- cards ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void ARulePreviewCardSaysWhatTheRuleWatchesDoesAndWhereItSends()
    {
        var answer = JsonNode.Parse("""
            {"change":"create","rule_id":null,"name":"Door alarm","enabled":true,"watches":"Input 1 of 3 devices","does":"Send an HTTP request",
             "stateful":false,"was":null,"changes_devices":false,"problem":"","warnings":["1 outside destination."],
             "destinations":[{"kind":"address","target":"http://10.0.0.5:8080","name":null,"saved_recipient":false,"new":true}],
             "handle":"h","expires":"2026-11-15T10:00:00Z"}
            """)!.AsObject();
        var card = AssistantCards.Build(Tool("oadm_rules_save_preview"), Tool("oadm_rules_save_run", destructive: true) with { Title = "Save a rule" }, "h", answer, NoArguments);

        Assert.Equal("Save a rule", card.Title);
        Assert.Equal("1 rule", card.CountText);
        Assert.False(card.Destructive); // a new rule deletes nothing
        Assert.Contains("Rule: Door alarm", card.Details);
        Assert.Contains("Creates the rule: on", card.Details);
        Assert.Contains("Watches: Input 1 of 3 devices", card.Details);
        Assert.Contains("Does: Send an HTTP request", card.Details);
        Assert.Contains("Sends to: http://10.0.0.5:8080", card.Details);
        Assert.DoesNotContain(card.Details, d => d.StartsWith("Problem", StringComparison.Ordinal));
        Assert.Equal(["1 outside destination."], card.Warnings);

        var delete = AssistantCards.Build(Tool("oadm_rules_delete_preview"), null, "h",
            JsonNode.Parse("""{"rule_id":"x","name":"Door alarm","enabled":true,"watches":"w","does":"d","state":"On","warnings":[],"handle":"h","expires":"e"}""")!.AsObject(), NoArguments);
        Assert.True(delete.Destructive);
        Assert.Contains("Deletes the rule: Door alarm", delete.Details);
    }

    [Fact]
    public void ATagPreviewCardNamesTheTagsAndAPluginPreviewListsItsPlainFieldsCutToTheLimit()
    {
        var tags = AssistantCards.Build(Tool("oadm_tags_assign_preview"), null, "h",
            JsonNode.Parse("""{"devices":3,"will_change":2,"gain":2,"lose":1,"new_tags":["Yard"],"destructive":true,"first":[{"id":"1","host_name":"cam-1"}],"warnings":[],"handle":"h","expires":"e"}""")!.AsObject(),
            JsonDocument.Parse("""{"add":["Lobby","Yard"],"remove":["Old"],"filter":{}}""").RootElement);
        Assert.Equal("2 devices", tags.CountText);
        Assert.Equal(2, tags.DeviceCount);
        Assert.True(tags.Destructive); // a tag is removed
        Assert.Equal(["Adds tags: Lobby, Yard", "Removes tags: Old", "New tags: Yard"], tags.Details);
        Assert.Equal(["cam-1"], tags.FirstDevices);

        // A plugin's preview: its plain fields, one level deep, never a secret; long values cut, at most 12 lines.
        var fields = new JsonObject
        {
            ["command"] = "library:set-ntp",
            ["kind"] = "change",
            ["request"] = new string('r', 500),
            ["will_run"] = 4,
            ["password"] = "hidden",
            ["values"] = new JsonObject { ["server"] = "10.0.0.1", ["token"] = "hidden" },
            ["skipped"] = new JsonArray(new JsonObject { ["reason"] = "Site offline", ["count"] = 1 }),
            ["handle"] = "h",
        };
        for (var i = 0; i < 20; i++)
        {
            fields["extra_" + i] = i;
        }

        var plugin = AssistantCards.Build(Tool("oadm_vapix_command_preview"), Tool("oadm_vapix_command_run", destructive: true) with { Title = "Run a command: run" }, "h", fields, NoArguments);
        Assert.Equal("Run a command", plugin.Title);
        Assert.Equal("4 devices", plugin.CountText);
        Assert.True(plugin.Destructive); // the run tool says it changes
        Assert.Equal("Command: library:set-ntp", plugin.Details[0]);
        Assert.Contains("Values, server: 10.0.0.1", plugin.Details);
        Assert.Equal(AssistantCards.MaxLine, plugin.Details.Single(d => d.StartsWith("Request", StringComparison.Ordinal)).Length);
        Assert.DoesNotContain(plugin.Details, d => d.Contains("hidden", StringComparison.Ordinal));
        Assert.Equal(AssistantCards.MaxLines, plugin.Details.Count);
        Assert.StartsWith("+", plugin.Details[^1], StringComparison.Ordinal);
        Assert.Equal(["1 device: Site offline"], plugin.Skipped);
    }

    // ---- limits ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ToolCallsThatActOutsideOadmCountPerSessionLikeOverMcp()
    {
        await using var server = await AssistantTestServer.StartAsync();
        var admin = (await server.Host.Get<UserStore>().FindByNameAsync(TestServerHost.AdminUserName, CancellationToken.None))!;
        var key = ToolHandles.BindingKeyOf(admin.Id, AuthTokenStore.HashOf(server.Host.AdminToken));
        var caller = new ToolCaller(new AuthenticatedCaller(admin.Id, admin.UserName, admin.RoleId, PermissionSet.All, "127.0.0.1", "x", AuthTokenStore.HashOf(server.Host.AdminToken)), key);
        Assert.True(server.Host.Get<ToolRegistry>().TryGet("oadm_devices_add_preview", out var scan));
        var limits = server.Host.Get<AssistantLimits>();
        for (var i = 0; i < AssistantLimits.ChangesPerMinute; i++)
        {
            Assert.Null(limits.Refusal(scan, caller));
        }

        Assert.True(server.Host.Get<ToolRegistry>().TryGet("oadm_devices_summary", out var read));
        Assert.Null(limits.Refusal(read, caller)); // a read counts against nothing

        server.Model.Call(("oadm_devices_add_preview", AssistantTestServer.Args("""{"addresses":["10.9.0.1"]}"""))).Say("Later.");
        var events = await server.ChatAsync("Scan");
        var line = Assert.Single(events, e => e.KindCase == Proto.AssistantEvent.KindOneofCase.Tool).Tool;
        Assert.True(line.Failed);
        Assert.Equal(AssistantTexts.TooManyChanges, line.Text);
        Assert.Equal(AssistantTexts.TooManyChanges, server.Model.Requests[1].Results().Values.Single()["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheSavedKeyGoesOnlyToItsProviderAndAddress()
    {
        await using var server = await AssistantTestServer.StartAsync(provider: AssistantTexts.OpenAiCompatible);
        var same = AssistantTestServer.Settings(AssistantTexts.OpenAiCompatible);
        same.ApiKey = string.Empty;
        Assert.True((await server.SetAsync(same)).HasApiKey);

        var moved = same.Clone();
        moved.Address = "http://attacker.example:8080/v1";
        var refused = await Assert.ThrowsAsync<RpcException>(async () => await server.Service().SetSettingsAsync(moved));
        Assert.Equal("api_key", refused.Trailers.GetValue(AssistantTexts.FieldTrailer));
        Assert.Equal(AssistantTexts.EnterApiKey, refused.Status.Detail);
        var test = await Assert.ThrowsAsync<RpcException>(async () => await server.Service().TestAsync(moved));
        Assert.Equal("api_key", test.Trailers.GetValue(AssistantTexts.FieldTrailer));
        Assert.DoesNotContain(server.Providers.Connections, c => c.Address == moved.Address);

        moved.ApiKey = "sk-new-key";
        Assert.True((await server.SetAsync(moved)).HasApiKey);
    }

    // ---- models list ---------------------------------------------------------------------------------------------------

    [Fact]
    public void OpenAisListKeepsItsOrderOfChatModelsAndTheSuggestionIsAKnownDefault()
    {
        string[] listed = ["babbage-002", "gpt-4.1-mini", "text-embedding-3-large", "o3", "tts-1", "whisper-1", "dall-e-3", "gpt-4o", "chatgpt-4o-latest", "omni-moderation-latest", "gpt-4o-realtime-preview"];
        var chat = AssistantProviders.ChatModels(AssistantTexts.OpenAi, listed);
        Assert.Equal(["gpt-4.1-mini", "o3", "gpt-4o", "chatgpt-4o-latest"], chat);
        Assert.Equal("gpt-4o", AssistantProviders.Suggest(AssistantTexts.OpenAi, chat));
        Assert.Equal(["z-model", "a-model"], AssistantProviders.ChatModels(AssistantTexts.Ollama, ["z-model", "a-model", "z-model"])); // the provider's order
        Assert.Equal("z-model", AssistantProviders.Suggest(AssistantTexts.Ollama, ["z-model", "a-model"]));
        Assert.Equal(AssistantTexts.AnthropicDefaultModel, AssistantProviders.Suggest(AssistantTexts.Anthropic, ["claude-haiku-5", AssistantTexts.AnthropicDefaultModel]));
    }

    [Fact]
    public async Task AFailedListIsNeverKeptAndTheLastGoodListStays()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var answers = new Queue<Func<IReadOnlyList<string>?>>();
        answers.Enqueue(() => throw new HttpRequestException("down"));
        answers.Enqueue(() => ["m1", "m2"]);
        answers.Enqueue(() => throw new HttpRequestException("down again"));
        var reads = 0;
        using var providers = new AssistantProviders(time, (_, _) =>
        {
            reads++;
            return Task.FromResult(answers.Dequeue()());
        });
        var connection = new AssistantConnection(AssistantTexts.Ollama, "http://10.0.0.5:11434", string.Empty, null);

        Assert.Empty(await providers.ModelsAsync(connection, CancellationToken.None));
        Assert.Equal(["m1", "m2"], await providers.ModelsAsync(connection, CancellationToken.None)); // the failure was not kept
        Assert.Equal(["m1", "m2"], await providers.ModelsAsync(connection, CancellationToken.None)); // kept: no new read
        Assert.Equal(2, reads);
        time.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(["m1", "m2"], await providers.ModelsAsync(connection, CancellationToken.None)); // a failed read keeps the last good list
        Assert.Equal(3, reads);
    }

    // ---- OADM's own failures, the daily estimate, Stop ------------------------------------------------------------------

    [Fact]
    public async Task AToolThatFailsInOadmIsAFailedLineAndTheAnswerGoesOn()
    {
        await using var server = await AssistantTestServer.StartAsync(configureServices: services =>
        {
            services.RemoveAll<ILiveVideoSourceFactory>();
            services.AddSingleton<ILiveVideoSourceFactory>(new BrokenCamera());
        });
        var device = await GroupCallers.DeviceAsync(server.Host, 91);
        server.Model.Call(("oadm_devices_snapshot", AssistantTestServer.Args($$$"""{"device_id":"{{{device.Id}}}"}"""))).Say("No picture.");

        var events = await server.ChatAsync("Picture");

        Assert.Contains(events, e => e.KindCase == Proto.AssistantEvent.KindOneofCase.Tool && e.Tool.Failed);
        Assert.Empty(events[^1].Done.Error);
        Assert.Equal("No picture.", AssistantTestServer.TextOf(events));
    }

    private sealed class BrokenCamera : ILiveVideoSourceFactory
    {
        public Task<LiveViewCapabilities> GetCapabilitiesAsync(Guid deviceId, CancellationToken ct) => throw new InvalidOperationException("broken");

        public Task<ILiveVideoSource> OpenAsync(LiveStreamKey key, CancellationToken ct) => throw new InvalidOperationException("broken");

        public Task<byte[]> GetSnapshotAsync(Guid deviceId, int camera, ImageSize size, int compression, CancellationToken ct) => throw new InvalidOperationException("broken");
    }

    [Fact]
    public void TheEstimateCountsAtOnceAndTheAnswerSettlesIt()
    {
        var usage = new AssistantUsage(TimeProvider.System);
        var anna = Guid.NewGuid();
        usage.Settle(anna, 0, 600); // the estimate at the start
        Assert.True(usage.Reached(anna, serverLimit: 500, userLimit: 0)); // a parallel message sees it already
        usage.Settle(anna, 600, 200); // the answer used less
        Assert.Equal(200, usage.UsedToday());
        Assert.Equal(200, usage.UsedToday(anna));
        usage.Settle(anna, 5_000, 0);
        Assert.Equal(0, usage.UsedToday()); // never below 0
    }

    [Fact]
    public async Task AStopJustBeforeTheAnswerBeganStopsIt()
    {
        await using var server = await AssistantTestServer.StartAsync(provider: AssistantTexts.Ollama);
        await server.ChatAsync("First");
        await server.Service().StopAsync(new Proto.AssistantConversationRequest { ConversationId = "chat-1" });
        server.Model.Hang();

        var events = await server.ChatAsync("Second");

        Assert.True(events[^1].Done.Stopped);
        server.Model.Say("Third.");
        Assert.False((await server.ChatAsync("Third"))[^1].Done.Stopped); // used once
    }

    // ---- time: 120 s, 30 minutes ----------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAnswerEndsAfter120SecondsWithThePlainText()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var server = await AssistantTestServer.StartAsync(provider: AssistantTexts.Ollama, configureServices: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(time);
        });
        server.Model.Hang();
        using var call = server.Service().Chat(new Proto.AssistantMessage { ConversationId = "chat-1", Text = "Slow", Model = "m" });
        Assert.True(await call.ResponseStream.MoveNext());
        Assert.Equal("Working", call.ResponseStream.Current.Text); // the answer runs: its time limit started

        time.Advance(AssistantChats.AnswerTimeout + TimeSpan.FromSeconds(1));

        Assert.True(await call.ResponseStream.MoveNext());
        Assert.Equal(AssistantTexts.NoAnswer, call.ResponseStream.Current.Done.Error);
        Assert.False(call.ResponseStream.Current.Done.Stopped);
    }

    [Fact]
    public void AChatEndsAfter30MinutesWithoutAMessage()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var conversations = new AssistantConversations(time);
        var user = Guid.NewGuid();
        var chat = conversations.Open("session", user, "chat-1");
        chat.Previews["handle"] = "oadm_tags_assign_run";
        time.Advance(AssistantConversations.Idle - TimeSpan.FromMinutes(1));
        Assert.Same(chat, conversations.Find("session", "chat-1"));
        time.Advance(AssistantConversations.Idle + TimeSpan.FromMinutes(1));
        Assert.Null(conversations.Find("session", "chat-1"));
        Assert.True(chat.Ended);
        Assert.Empty(chat.Previews);
    }

    // ---- context ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void OneMessageWithManyLargeToolAnswersFitsAndTheNewestStaysWhole()
    {
        var big = new string('x', 30_000);
        List<ChatMessage> turn = [new(ChatRole.User, "Look")];
        for (var i = 0; i < 20; i++)
        {
            turn.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c" + i, "oadm_logs_search")]));
            turn.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c" + i, big)]));
        }

        var shortened = AssistantChats.Shorten(turn);

        Assert.InRange(shortened.Sum(AssistantChats.SizeOf), 1, AssistantChats.MaxContextCharacters);
        var results = shortened.SelectMany(m => m.Contents).OfType<FunctionResultContent>().ToList();
        Assert.Equal(big, results[^1].Result);
        Assert.All(results.Take(19), r => Assert.True(r.Result!.ToString()!.Length < 2_000));
        var small = turn.Take(3).ToList();
        Assert.Same(small, AssistantChats.Shorten(small)); // a turn that fits stays as it is
    }

    [Fact]
    public async Task OldTurnsBeyondTheContextAreDropped()
    {
        await using var server = await AssistantTestServer.StartAsync(provider: AssistantTexts.Ollama);
        var long_ = new string('a', 90_000);
        for (var i = 0; i < 4; i++)
        {
            server.Model.Say(long_);
            await server.ChatAsync("Message " + i);
        }

        var chat = server.Host.Get<AssistantConversations>().Find(AuthTokenStore.HashOf(server.Host.AdminToken), "chat-1")!;
        Assert.InRange(chat.Turns.Count, 1, 3);
        Assert.InRange(chat.Turns.Sum(t => t.Sum(AssistantChats.SizeOf)), 1, AssistantChats.MaxContextCharacters);
    }
}
