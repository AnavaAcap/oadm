using System.Globalization;
using System.Text.Json.Nodes;

using static Oadm.Server.Tools.ToolSchema;

namespace Oadm.Server.Tools;

/// <summary>
/// A small list of a tool (tags, sites): sorted by a key, a page of 50 rows by default (at most 200), the total and a sort-key
/// cursor (rule 3). The lists are short (at most a few hundred rows), so a call reads the whole list and cuts the page.
/// </summary>
internal static class AreaListing
{
    /// <summary>The answer to a run tool whose handle is unknown, ended, used or of another caller.</summary>
    public const string PreviewEnded = "This preview has ended or already ran. Call the preview tool again.";

    /// <summary>The output schema of a device row (the fields of <c>DeviceTools.Row</c> without extra fields, plus tags).</summary>
    public static JsonObject DeviceRowSchema() => OutObject(null,
    [
        P("id", Out("string", "The device id.")),
        P("address", Out("string", "The address OADM reaches it on.")),
        P("host_name", Out("string", "Its host name; empty when not known.")),
        P("upnp_name", Out("string", "Its UPnP name; empty when not known.")),
        P("model", Out("string", "The model.")),
        P("firmware", Out("string", "The AXIS OS version.")),
        P("status", Out("string", "Its status.")),
        P("tags", OutList("Its tags.", Out("string", "A tag."))),
    ], "id", "address", "host_name", "upnp_name", "model", "firmware", "status");

    /// <summary>The page after the cursor: its rows, whether more follow and the cursor of the next page.</summary>
    public static (JsonArray Rows, string? Next) Page<T>(ToolCall call, ToolCursors cursors, IReadOnlyList<T> sorted, Func<T, string> key, Func<T, string> id, Func<T, JsonObject> row)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(cursors);
        ArgumentNullException.ThrowIfNull(sorted);
        var query = ToolCursors.QueryOf(call.Arguments);
        var after = cursors.Read(call.Caller, call.Text("cursor"), query);
        var limit = call.Number("limit") ?? ToolAnswers.DefaultRows;
        var rows = new JsonArray();
        var size = 512;
        T? last = default;
        var more = false;
        foreach (var item in sorted)
        {
            if (after is { } a && Compare(key(item), id(item), a.Key, a.Id) <= 0)
            {
                continue;
            }

            var json = row(item);
            var rowSize = ToolAnswers.SizeOf(json) + 1;
            if (rows.Count >= limit || (size + rowSize > ToolAnswers.MaxBytes && rows.Count > 0))
            {
                more = true;
                break;
            }

            size += rowSize;
            rows.Add(json);
            last = item;
        }

        return (rows, more && last is not null ? cursors.Make(call.Caller, query, key(last), id(last)) : null);
    }

    /// <summary>The order of the lists: the key without regard to case, then the id.</summary>
    public static int Compare(string keyA, string idA, string keyB, string idB)
    {
        var byKey = string.Compare(keyA, keyB, StringComparison.OrdinalIgnoreCase);
        return byKey != 0 ? byKey : string.CompareOrdinal(idA, idB);
    }

    public static IReadOnlyList<T> Sort<T>(IEnumerable<T> items, Func<T, string> key, Func<T, string> id) =>
    [.. items.OrderBy(key, StringComparer.OrdinalIgnoreCase).ThenBy(id, StringComparer.Ordinal)];

    public static string Time(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static string Time(Google.Protobuf.WellKnownTypes.Timestamp? time) =>
        time is null || time.Seconds == 0 ? string.Empty : time.ToDateTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
