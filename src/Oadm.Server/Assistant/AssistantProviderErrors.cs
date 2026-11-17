using System.Net;
using System.Reflection;
using System.Text.Json;

namespace Oadm.Server.Assistant;

/// <summary>
/// A provider's error in plain words (docs/spec/assistant.md "Settings", Test): the refused key, an unknown model, the limit of
/// the account, no connection. The key never shows: it is cut out of every text that could hold it.
/// </summary>
internal static class AssistantProviderErrors
{
    public const string KeyRefused = "The provider refused the API key.";
    public const string NotFound = "The provider does not know this model or address.";
    public const string Busy = "The provider is busy or the account reached its limit. Try again later.";
    public const string Unreachable = "The provider cannot be reached. Check the address and the server's connection.";

    public static string Plain(Exception exception, string? apiKey)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var status = StatusOf(exception);
        if (status is 401 or 403)
        {
            return KeyRefused;
        }

        if (status == 404)
        {
            return NotFound;
        }

        if (status == 429)
        {
            return Busy;
        }

        if (status is null && Chain(exception).Any(e => e is HttpRequestException or System.Net.Sockets.SocketException or System.Security.Authentication.AuthenticationException))
        {
            return Unreachable;
        }

        var text = DetailOf(exception) ?? exception.Message.Split('\n', 2)[0].Trim();
        if (!string.IsNullOrEmpty(apiKey))
        {
            text = text.Replace(apiKey, "***", StringComparison.Ordinal);
        }

        if (text.Length > 200)
        {
            text = text[..199] + "…";
        }

        return "The provider answered with an error: " + (text.Length == 0 ? exception.GetType().Name : text);
    }

    /// <summary>
    /// The chosen model refused the tools: the providers answer 400 with a text like "llama2 does not support tools" (Ollama)
    /// or "tools is not supported with this model" (OpenAI-compatible servers); an SDK may say NotSupported.
    /// </summary>
    public static bool RejectsTools(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (StatusOf(exception) is { } status && status is not (400 or 422 or 501))
        {
            return false;
        }

        return Chain(exception).Any(e => e is NotSupportedException
            || (e.Message.Contains("tool", StringComparison.OrdinalIgnoreCase)
                && (e.Message.Contains("support", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("not allowed", StringComparison.OrdinalIgnoreCase))));
    }

    /// <summary>The HTTP status of the SDKs' errors: <see cref="HttpRequestException.StatusCode"/> or a <c>Status</c> / <c>StatusCode</c> property.</summary>
    private static int? StatusOf(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException { StatusCode: { } code })
            {
                return (int)code;
            }

            foreach (var name in new[] { "Status", "StatusCode" })
            {
                var value = e.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(e);
                switch (value)
                {
                    case int number when number >= 100:
                        return number;
                    case HttpStatusCode http:
                        return (int)http;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The provider's own reason of a refused request: the Anthropic SDK keeps it in <c>ResponseBody</c>
    /// (<c>{"error":{"message":"..."}}</c>) and its message says only "Status Code: BadRequest".
    /// </summary>
    private static string? DetailOf(Exception exception)
    {
        foreach (var e in Chain(exception))
        {
            if (e.GetType().GetProperty("ResponseBody", BindingFlags.Public | BindingFlags.Instance)?.GetValue(e) is not string { Length: > 0 } body)
            {
                continue;
            }

            try
            {
                using var json = JsonDocument.Parse(body);
                if (json.RootElement.ValueKind == JsonValueKind.Object
                    && json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var message) && message.GetString() is { Length: > 0 } text)
                {
                    return text.Split('\n', 2)[0].Trim();
                }
            }
            catch (JsonException)
            {
                // not the provider's JSON: the exception's own message
            }
        }

        return null;
    }

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            yield return e;
        }
    }
}
