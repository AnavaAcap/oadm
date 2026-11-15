using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Oadm.Server.Tools;

/// <summary>A place in a list: the sort key and the id of the last row of a page (the next page starts after it).</summary>
internal readonly record struct ToolCursor(string Key, string Id);

/// <summary>
/// The cursors of the lists (rule 3): the last row's sort key and id, so a row added, removed or changed elsewhere never
/// moves the rows between pages (docs/spec/mcp.md "Stage 1 as built"). Bound to the caller (the access key over MCP) and the query (a cursor of
/// another key or another search is refused) and signed with a key of this server process: a restart ends every cursor.
/// </summary>
internal sealed class ToolCursors
{
    public const string NotValid = "This cursor does not belong to this search. Search again without it.";

    private readonly byte[] _secret = RandomNumberGenerator.GetBytes(32);

    /// <summary>The cursor of the page after the row with <paramref name="key"/> and <paramref name="id"/>.</summary>
    public string Make(ToolCaller caller, string query, string key, string id)
    {
        var payload = Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { key, id })));
        return payload + "." + Mac(caller, query, payload);
    }

    /// <summary>
    /// The place of <paramref name="cursor"/>; null without one. <see cref="ToolException"/> with <see cref="NotValid"/> for a
    /// cursor of another key or query, or one that was changed.
    /// </summary>
    public ToolCursor? Read(ToolCaller caller, string? cursor, string query)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return null;
        }

        var dot = cursor.LastIndexOf('.');
        if (dot <= 0
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Mac(caller, query, cursor[..dot])), Encoding.ASCII.GetBytes(cursor[(dot + 1)..])))
        {
            throw Refused();
        }

        try
        {
            var parts = JsonSerializer.Deserialize<string[]>(FromBase64Url(cursor[..dot]));
            return parts is [{ } key, { } id] ? new ToolCursor(key, id) : throw Refused();
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw Refused();
        }
    }

    /// <summary>The query of a call of a list tool: the tool's name and its arguments (a cursor of another tool is refused).</summary>
    public static string QueryOf(ToolCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return call.Tool + "|" + QueryOf(call.Arguments);
    }

    /// <summary>The query of a call: its arguments without the cursor and the page size, with sorted keys.</summary>
    public static string QueryOf(JsonElement arguments)
    {
        var builder = new StringBuilder();
        Write(arguments, builder, top: true);
        return builder.ToString();
    }

    private static ToolException Refused() => new(NotValid, [new ToolFieldError("cursor", NotValid)]);

    private static void Write(JsonElement value, StringBuilder builder, bool top)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                foreach (var property in value.EnumerateObject().Where(p => !top || p.Name is not ("cursor" or "limit")).OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    builder.Append(JsonSerializer.Serialize(property.Name)).Append(':');
                    Write(property.Value, builder, top: false);
                    builder.Append(',');
                }

                builder.Append('}');
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                foreach (var item in value.EnumerateArray())
                {
                    Write(item, builder, top: false);
                    builder.Append(',');
                }

                builder.Append(']');
                break;
            default:
                builder.Append(value.GetRawText());
                break;
        }
    }

    private string Mac(ToolCaller caller, string query, string payload)
    {
        var data = Encoding.UTF8.GetBytes(caller.BindingKey + "\n" + query + "\n" + payload);
        return Base64Url(HMACSHA256.HashData(_secret, data).AsSpan(0, 16));
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded + new string('=', (4 - (padded.Length % 4)) % 4));
    }
}
