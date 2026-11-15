using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

using Grpc.Core;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Oadm.Contracts.Assistant;
using Oadm.Server.Assistant;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Assistant;

/// <summary>What the fake model was sent in one request.</summary>
internal sealed record ModelRequest(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)
{
    /// <summary>The names of the tools offered.</summary>
    public IReadOnlyList<string> Tools => [.. (Options?.Tools ?? []).Select(t => t.Name)];

    /// <summary>The results of the tool calls in the last message, by call id, as JSON.</summary>
    public IReadOnlyDictionary<string, JsonObject> Results() =>
        Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .GroupBy(r => r.CallId).ToDictionary(g => g.Key, g => JsonNode.Parse(g.Last().Result!.ToString()!)!.AsObject());

    /// <summary>Every text of the request (system, user, assistant, tool results) in one string.</summary>
    public string AllText() => string.Join("\n", Messages.SelectMany(m => m.Contents).Select(c => c switch
    {
        TextContent t => t.Text,
        FunctionResultContent r => r.Result?.ToString(),
        FunctionCallContent f => f.Name + JsonSerializer.Serialize(f.Arguments),
        _ => c.GetType().Name,
    }));
}

/// <summary>
/// A model that answers from a script (no network): each request takes the next step, which streams text, asks for tool
/// calls or waits until it is stopped. It records every request it got. Without a step it answers "Done.".
/// </summary>
internal sealed class FakeChatClient : IChatClient
{
    private readonly Lock _gate = new();
    private readonly Queue<Func<ModelRequest, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>> _steps = new();
    private readonly List<ModelRequest> _requests = [];

    public IReadOnlyList<ModelRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Tokens each answer reports.</summary>
    public long TokensPerAnswer { get; set; } = 10;

    public FakeChatClient Then(Func<ModelRequest, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> step)
    {
        lock (_gate)
        {
            _steps.Enqueue(step);
        }

        return this;
    }

    /// <summary>The next answer is <paramref name="text"/> in two pieces.</summary>
    public FakeChatClient Say(string text) => Then((_, _) => Updates(this, [new TextContent(text[..(text.Length / 2)]), new TextContent(text[(text.Length / 2)..])]));

    /// <summary>The next answer streams exactly these pieces (a marker cut anywhere).</summary>
    public FakeChatClient SayPieces(params string[] pieces) => Then((_, _) => Updates(this, [.. pieces.Select(p => (AIContent)new TextContent(p))]));

    /// <summary>The next answer asks for these tool calls (name and arguments).</summary>
    public FakeChatClient Call(params (string Name, JsonObject? Arguments)[] calls) =>
        Then((_, _) => Updates(this, [.. calls.Select((c, i) => (AIContent)new FunctionCallContent("call-" + Guid.NewGuid().ToString("N")[..8] + i, c.Name, ArgumentsOf(c.Arguments)))]));

    /// <summary>The next answer streams exactly these contents (a provider's own web search steps, text).</summary>
    public FakeChatClient Stream(params AIContent[] contents) => Then((_, _) => Updates(this, contents));

    /// <summary>The next answer streams a piece, then waits until the request is cancelled (Stop, End).</summary>
    public FakeChatClient Hang() => Then(HangAsync);

    private static async IAsyncEnumerable<ChatResponseUpdate> HangAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, "Working");
        await Task.Delay(Timeout.Infinite, ct);
    }

    public static Dictionary<string, object?> ArgumentsOf(JsonObject? arguments) =>
        (arguments ?? []).ToDictionary(p => p.Key, p => (object?)JsonSerializer.SerializeToElement(p.Value));

    private static async IAsyncEnumerable<ChatResponseUpdate> Updates(FakeChatClient client, IEnumerable<AIContent> contents)
    {
        foreach (var content in contents)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [content], MessageId = "m1" };
        }

        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new UsageContent(new UsageDetails { TotalTokenCount = client.TokensPerAnswer })], MessageId = "m1" };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = new ModelRequest([.. messages], options);
        Func<ModelRequest, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> step;
        lock (_gate)
        {
            _requests.Add(request);
            step = _steps.Count > 0 ? _steps.Dequeue() : (_, _) => Updates(this, [new TextContent("Done.")]);
        }

        await foreach (var update in step(request, cancellationToken).WithCancellation(cancellationToken))
        {
            yield return update;
        }
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            updates.Add(update);
        }

        return updates.ToChatResponse();
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
        // Shared by every answer of a test.
    }
}

/// <summary>The providers of the tests: every connection gets the one fake model; the connections are recorded.</summary>
internal sealed class FakeProviders(FakeChatClient model) : IAssistantProviders
{
    private readonly Lock _gate = new();
    private readonly List<AssistantConnection> _connections = [];

    public IReadOnlyList<AssistantConnection> Connections
    {
        get
        {
            lock (_gate)
            {
                return [.. _connections];
            }
        }
    }

    public IReadOnlyList<string> Models { get; set; } = ["model-a", "model-b"];

    public IChatClient Create(AssistantConnection connection)
    {
        lock (_gate)
        {
            _connections.Add(connection);
        }

        return model;
    }

    public Task<IReadOnlyList<string>> ModelsAsync(AssistantConnection connection, CancellationToken ct) => Task.FromResult(Models);

    /// <summary>What Test reads: the list, null (no list) or the provider's refusal.</summary>
    public Func<AssistantConnection, IReadOnlyList<string>?> Read { get; set; } = _ => ["model-a", "model-b"];

    public Task<IReadOnlyList<string>?> ReadModelsAsync(AssistantConnection connection, CancellationToken ct)
    {
        lock (_gate)
        {
            _connections.Add(connection);
        }

        return Task.FromResult(Read(connection));
    }
}

/// <summary>The real server with the fake model and the assistant turned on by the administrator.</summary>
internal sealed class AssistantTestServer : IAsyncDisposable
{
    public const string ApiKey = "sk-canary-assistant-5c1e9a";

    private AssistantTestServer(TestServerHost host, FakeChatClient model, FakeProviders providers)
    {
        Host = host;
        Model = model;
        Providers = providers;
    }

    public TestServerHost Host { get; }

    public FakeChatClient Model { get; }

    public FakeProviders Providers { get; }

    public static async Task<AssistantTestServer> StartAsync(FakeAxisNetwork? network = null, bool turnOn = true, string provider = AssistantTexts.Anthropic,
        bool sendPictures = false, long dailyLimit = AssistantTexts.DefaultDailyLimit, long userDailyLimit = 0, Action<IServiceCollection>? configureServices = null)
    {
        var model = new FakeChatClient();
        var providers = new FakeProviders(model);
        var host = await TestServerHost.StartAsync(network, configureServices: services =>
        {
            services.RemoveAll<IAssistantProviders>();
            services.AddSingleton<IAssistantProviders>(providers);
            configureServices?.Invoke(services);
        });
        var server = new AssistantTestServer(host, model, providers);
        if (turnOn)
        {
            await server.SetAsync(Settings(provider, sendPictures, dailyLimit, userDailyLimit));
        }

        return server;
    }

    public static Proto.AssistantSettings Settings(string provider = AssistantTexts.Anthropic, bool sendPictures = false, long dailyLimit = AssistantTexts.DefaultDailyLimit,
        long userDailyLimit = 0, bool enabled = true) => new()
    {
        Enabled = enabled,
        Provider = provider,
        Address = AssistantSettings.NeedsAddress(provider) ? "http://10.0.0.5:11434" : string.Empty,
        ApiKey = AssistantSettings.NeedsApiKey(provider) ? ApiKey : string.Empty,
        SendPictures = sendPictures,
        DailyLimit = dailyLimit,
        UserDailyLimit = userDailyLimit,
    };

    public Proto.AssistantService.AssistantServiceClient Service(CallInvoker? invoker = null) => new(invoker ?? Host.Invoker);

    public async Task<Proto.AssistantSettingsState> SetAsync(Proto.AssistantSettings settings) => await Service().SetSettingsAsync(settings);

    /// <summary>One message and every event of its answer.</summary>
    public async Task<List<Proto.AssistantEvent>> ChatAsync(string text, string conversation = "chat-1", CallInvoker? invoker = null,
        IEnumerable<string>? selected = null, IEnumerable<Proto.AssistantAttachment>? attachments = null, string model = "test-model", bool replaceLast = false, string docsOffer = "")
    {
        var message = new Proto.AssistantMessage { ConversationId = conversation, Text = text, Model = model, ReplaceLast = replaceLast, DocsOfferId = docsOffer };
        message.SelectedDeviceIds.AddRange(selected ?? []);
        message.Attachments.AddRange(attachments ?? []);
        using var call = Service(invoker).Chat(message);
        var events = new List<Proto.AssistantEvent>();
        await foreach (var e in call.ResponseStream.ReadAllAsync())
        {
            events.Add(e);
        }

        Assert.Equal(Proto.AssistantEvent.KindOneofCase.Done, events[^1].KindCase);
        return events;
    }

    public static string TextOf(IEnumerable<Proto.AssistantEvent> events) => string.Concat(events.Where(e => e.KindCase == Proto.AssistantEvent.KindOneofCase.Text).Select(e => e.Text));

    public static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    public ValueTask DisposeAsync() => Host.DisposeAsync();
}
