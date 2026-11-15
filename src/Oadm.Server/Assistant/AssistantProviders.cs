using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.AI;

using Oadm.Contracts.Assistant;

namespace Oadm.Server.Assistant;

/// <summary>What one call to a provider needs: the provider, its address, the model and the key (in memory for this call only).</summary>
internal sealed record AssistantConnection(string Provider, string? Address, string Model, string? ApiKey)
{
    /// <summary>Never the key (records print every property).</summary>
    public override string ToString() => $"{AssistantTexts.ProviderName(Provider)} {Model}";
}

/// <summary>The providers of the assistant (docs/spec/assistant.md "Where it runs"); tests give a fake.</summary>
internal interface IAssistantProviders
{
    /// <summary>A chat client of <paramref name="connection"/>; the caller disposes it after the answer.</summary>
    IChatClient Create(AssistantConnection connection);

    /// <summary>The models the provider offers; empty when it offers no list or does not answer in time (best effort).</summary>
    Task<IReadOnlyList<string>> ModelsAsync(AssistantConnection connection, CancellationToken ct);

    /// <summary>
    /// Reads the model list now (Test): the list; null when the provider offers none (HTTP 404); the provider's refusal as
    /// <see cref="HttpRequestException"/> with its status, or the connection's error.
    /// </summary>
    Task<IReadOnlyList<string>?> ReadModelsAsync(AssistantConnection connection, CancellationToken ct);
}

/// <summary>
/// The real providers: Anthropic through the official <c>Anthropic</c> SDK's <see cref="IChatClient"/>, OpenAI and
/// OpenAI-compatible ones through <c>Microsoft.Extensions.AI.OpenAI</c>, Ollama through <c>OllamaSharp</c>. One HTTP handler
/// for all of them (never to a device); the time limit of an answer is the caller's token. The models lists are read over
/// plain HTTP, kept for 10 minutes for the panel's picker and never wait longer than <see cref="ModelsWait"/>.
/// </summary>
[SuppressMessage("ApiDesign", "RS0030:Do not use banned APIs", Justification = "The AI provider the administrator chose, never a device.")]
internal sealed class AssistantProviders(TimeProvider time, Func<AssistantConnection, CancellationToken, Task<IReadOnlyList<string>?>>? read = null)
    : IAssistantProviders, IDisposable
{
    /// <summary>How long GetSettings waits for a models list at most.</summary>
    public static readonly TimeSpan ModelsWait = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan ModelsKept = TimeSpan.FromMinutes(10);
    private static readonly Uri AnthropicApi = new("https://api.anthropic.com/");
    private static readonly Uri OpenAiApi = new("https://api.openai.com/v1/");

    /// <summary>The longest answer of a model in tokens.</summary>
    public const int MaxOutputTokens = 4_096;

    private readonly SocketsHttpHandler _handler = new() { PooledConnectionLifetime = TimeSpan.FromMinutes(5), ConnectTimeout = TimeSpan.FromSeconds(15) };
    /// <summary>The last good list per connection (a failed read never replaces it) and the read that runs now.</summary>
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, IReadOnlyList<string> List)> _good = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<IReadOnlyList<string>?>> _reading = new(StringComparer.Ordinal);

    private HttpClient Http(Uri? baseAddress = null)
    {
        var http = new HttpClient(_handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan, BaseAddress = baseAddress };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OADM-Server/" + Mapping.Mappers.ServerVersion);
        return http;
    }

    public IChatClient Create(AssistantConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.Provider switch
        {
            AssistantTexts.Anthropic => new Anthropic.AnthropicClient { ApiKey = connection.ApiKey ?? string.Empty, HttpClient = Http(), MaxRetries = 1 }
                .AsIChatClient(connection.Model, MaxOutputTokens),
            AssistantTexts.OpenAi => OpenAi(connection, OpenAiApi),
            AssistantTexts.OpenAiCompatible => OpenAi(connection, BaseOf(connection.Address)),
            AssistantTexts.Ollama => new OllamaSharp.OllamaApiClient(Http(BaseOf(connection.Address)), connection.Model),
            _ => throw new ArgumentException("Unknown provider.", nameof(connection)),
        };
    }

    private IChatClient OpenAi(AssistantConnection connection, Uri endpoint)
    {
        var options = new OpenAI.OpenAIClientOptions { Endpoint = endpoint, Transport = new HttpClientPipelineTransport(Http()), NetworkTimeout = Timeout.InfiniteTimeSpan };
        return new OpenAI.OpenAIClient(new ApiKeyCredential(string.IsNullOrEmpty(connection.ApiKey) ? "none" : connection.ApiKey), options)
            .GetChatClient(connection.Model).AsIChatClient();
    }

    /// <summary>The address as a base URI that ends with a slash (relative paths of the SDKs go below it).</summary>
    public static Uri BaseOf(string? address)
    {
        var text = (address ?? string.Empty).Trim();
        return new Uri(text.EndsWith('/') ? text : text + "/");
    }

    public async Task<IReadOnlyList<string>> ModelsAsync(AssistantConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var key = connection.Provider + "|" + connection.Address + "|" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(connection.ApiKey ?? string.Empty)));
        var good = _good.TryGetValue(key, out var kept) ? kept : ((DateTimeOffset At, IReadOnlyList<string> List)?)null;
        if (good is { } fresh && time.GetUtcNow() - fresh.At <= ModelsKept)
        {
            return fresh.List;
        }

        var reading = _reading.GetOrAdd(key, _ => FetchAsync(key, connection));
        try
        {
            return await reading.WaitAsync(ModelsWait, time, ct).ConfigureAwait(false) ?? good?.List ?? [];
        }
        catch (TimeoutException)
        {
            return good?.List ?? []; // the new list comes with a later read
        }
        finally
        {
            if (reading.IsCompleted)
            {
                _reading.TryRemove(new KeyValuePair<string, Task<IReadOnlyList<string>?>>(key, reading)); // a read that ended at once is never reused
            }
        }
    }

    /// <summary>One read of the list: a good one is kept for <see cref="ModelsKept"/>; a failure (null) is never kept.</summary>
    private async Task<IReadOnlyList<string>?> FetchAsync(string key, AssistantConnection connection)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10), time);
            var list = await (read ?? ReadModelsAsync)(connection, cts.Token).ConfigureAwait(false) ?? [];
            _good[key] = (time.GetUtcNow(), list);
            return list;
        }
#pragma warning disable CA1031 // A models list is best effort: whatever goes wrong, the last good list stays and the panel lets the user type.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
        finally
        {
            _reading.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// The provider's chat models in its own order (Anthropic and OpenAI answer the newest first): for OpenAI only the GPT,
    /// ChatGPT and o-series models, never embeddings, speech, images or the old completion models.
    /// </summary>
    public static IReadOnlyList<string> ChatModels(string provider, IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var clean = ids.Where(n => !string.IsNullOrWhiteSpace(n) && n.Length <= 200).Distinct(StringComparer.Ordinal);
        if (provider == AssistantTexts.OpenAi)
        {
            string[] notChat = ["embedding", "tts", "whisper", "dall-e", "davinci", "babbage", "moderation", "audio", "realtime", "transcribe", "image", "search"];
            clean = clean.Where(n => (n.StartsWith("gpt-", StringComparison.Ordinal) || n.StartsWith("chatgpt-", StringComparison.Ordinal)
                    || (n.Length > 1 && n[0] == 'o' && char.IsAsciiDigit(n[1])))
                && !notChat.Any(w => n.Contains(w, StringComparison.OrdinalIgnoreCase)));
        }

        return [.. clean.Take(200)];
    }

    /// <summary>The models the panel suggests first, per provider, when the provider lists one of them.</summary>
    private static readonly Dictionary<string, string[]> Defaults = new(StringComparer.Ordinal)
    {
        [AssistantTexts.Anthropic] = [AssistantTexts.AnthropicDefaultModel],
        [AssistantTexts.OpenAi] = ["gpt-5.5", "gpt-5", "gpt-4.1", "gpt-4o"],
    };

    /// <summary>The suggested model: the provider's known default when listed, else the first listed (the provider's order).</summary>
    public static string Suggest(string provider, IReadOnlyList<string> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        var known = Defaults.GetValueOrDefault(provider) ?? [];
        return known.FirstOrDefault(models.Contains)
            ?? (models.Count > 0 ? models[0] : provider == AssistantTexts.Anthropic ? AssistantTexts.AnthropicDefaultModel : string.Empty);
    }

    public async Task<IReadOnlyList<string>?> ReadModelsAsync(AssistantConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        {
            using var http = Http();
            using var request = connection.Provider switch
            {
                AssistantTexts.Anthropic => new HttpRequestMessage(HttpMethod.Get, new Uri(AnthropicApi, "v1/models?limit=100")),
                AssistantTexts.OpenAi => new HttpRequestMessage(HttpMethod.Get, new Uri(OpenAiApi, "models")),
                AssistantTexts.OpenAiCompatible => new HttpRequestMessage(HttpMethod.Get, new Uri(BaseOf(connection.Address), "models")),
                _ => new HttpRequestMessage(HttpMethod.Get, new Uri(BaseOf(connection.Address), "api/tags")),
            };
            if (connection.Provider == AssistantTexts.Anthropic)
            {
                request.Headers.Add("x-api-key", connection.ApiKey ?? string.Empty);
                request.Headers.Add("anthropic-version", "2023-06-01");
            }
            else if (!string.IsNullOrEmpty(connection.ApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.ApiKey);
            }

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null; // no list at this address
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException("The provider refused the models list.", null, response.StatusCode);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var (list, field) = connection.Provider == AssistantTexts.Ollama ? ("models", "name") : ("data", "id");
            if (!json.RootElement.TryGetProperty(list, out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return ChatModels(connection.Provider, items.EnumerateArray()
                .Select(i => i.TryGetProperty(field, out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() ?? string.Empty : string.Empty));
        }
    }

    public void Dispose() => _handler.Dispose();
}
