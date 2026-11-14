using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Oadm.Server.Mcp;

/// <summary>
/// The cursors of the lists (rule 3): where the next page starts, bound to the access key and the query (a cursor of another
/// key or another search is refused) and, for devices, to the device table's version (after a device was added or removed it
/// answers <see cref="DevicesChanged"/>). Signed with a key of this server process: a restart ends every cursor.
/// </summary>
internal sealed class McpCursors
{
    public const string DevicesChanged = "The device list changed. Search again.";
    public const string NotValid = "This cursor does not belong to this search. Search again without it.";

    private readonly byte[] _secret = RandomNumberGenerator.GetBytes(32);

    /// <summary>The cursor of the page that starts at <paramref name="offset"/>.</summary>
    public string Make(McpCaller caller, string query, long version, int offset)
    {
        var payload = string.Create(CultureInfo.InvariantCulture, $"{offset}.{version}");
        return payload + "." + Mac(caller, query, payload);
    }

    /// <summary>
    /// The offset of <paramref name="cursor"/> (0 without one); <see cref="McpToolException"/> with <see cref="NotValid"/> for a
    /// cursor of another key or query, with <paramref name="changed"/> when the version moved on.
    /// </summary>
    public int Read(McpCaller caller, string? cursor, string query, long version, string changed = DevicesChanged)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return 0;
        }

        var parts = cursor.Split('.');
        if (parts.Length != 3
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
            || !long.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var madeAt)
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Mac(caller, query, parts[0] + "." + parts[1])), Encoding.ASCII.GetBytes(parts[2])))
        {
            throw new McpToolException(NotValid, [new McpFieldError("cursor", NotValid)]);
        }

        return madeAt == version ? offset : throw new McpToolException(changed, [new McpFieldError("cursor", changed)]);
    }

    /// <summary>The query of a call: its arguments without the cursor and the page size, with sorted keys.</summary>
    public static string QueryOf(JsonElement arguments)
    {
        var builder = new StringBuilder();
        Write(arguments, builder, top: true);
        return builder.ToString();
    }

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

    private string Mac(McpCaller caller, string query, string payload)
    {
        var data = Encoding.UTF8.GetBytes(caller.KeyHash + "\n" + query + "\n" + payload);
        return Convert.ToBase64String(HMACSHA256.HashData(_secret, data).AsSpan(0, 16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
