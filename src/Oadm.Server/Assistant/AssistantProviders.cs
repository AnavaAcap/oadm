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
}

/// <summary>
/// The real providers: Anthropic through the official <c>Anthropic</c> SDK's <see cref="IChatClient"/>, OpenAI and
/// OpenAI-compatible ones through <c>Microsoft.Extensions.AI.OpenAI</c>, Ollama through <c>OllamaSharp</c>. One HTTP handler
/// for all of them (never to a device); the time limit of an answer is the caller's token. The models lists are read over
/// plain HTTP, kept for 10 minutes and never wait longer than <see cref="ModelsWait"/>.
/// </summary>
[SuppressMessage("ApiDesign", "RS0030:Do not use banned APIs", Justification = "The AI provider the administrator chose, never a device.")]
internal sealed class AssistantProviders(TimeProvider time) : IAssistantProviders, IDisposable
{
    /// <summary>How long GetSettings waits for a models list at most.</summary>
    public static readonly TimeSpan ModelsWait = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan ModelsKept = TimeSpan.FromMinutes(10);
    private static readonly Uri AnthropicApi = new("https://api.anthropic.com/");
    private static readonly Uri OpenAiApi = new("https://api.openai.com/v1/");

    /// <summary>The longest answer of a model in tokens.</summary>
    public const int MaxOutputTokens = 4_096;

    private readonly SocketsHttpHandler _handler = new() { PooledConnectionLifetime = TimeSpan.FromMinutes(5), ConnectTimeout = TimeSpan.FromSeconds(15) };
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, Task<IReadOnlyList<string>> List)> _models = new(StringComparer.Ordinal);

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
        var now = time.GetUtcNow();
        var entry = _models.AddOrUpdate(key,
            _ => (now, FetchAsync(connection)),
            (_, old) => now - old.At > ModelsKept || old.List.IsFaulted ? (now, FetchAsync(connection)) : old);
        try
        {
            return await entry.List.WaitAsync(ModelsWait, time, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return []; // the list comes with the next read
        }
    }

    private async Task<IReadOnlyList<string>> FetchAsync(AssistantConnection connection)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10), time);
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

            using var response = await http.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);
            var (list, field) = connection.Provider == AssistantTexts.Ollama ? ("models", "name") : ("data", "id");
            if (!json.RootElement.TryGetProperty(list, out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return
            [
                .. items.EnumerateArray()
                    .Select(i => i.TryGetProperty(field, out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null)
                    .Where(n => !string.IsNullOrWhiteSpace(n) && n.Length <= 200)
                    .Select(n => n!)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .Take(200),
            ];
        }
#pragma warning disable CA1031 // A models list is best effort: whatever goes wrong, the card shows no list.
        catch (Exception)
#pragma warning restore CA1031
        {
            return [];
        }
    }

    public void Dispose() => _handler.Dispose();
}
