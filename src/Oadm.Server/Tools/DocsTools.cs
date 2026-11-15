using System.Text.Json.Nodes;

using Oadm.Server.Assistant;

using static Oadm.Server.Tools.ToolSchema;

namespace Oadm.Server.Tools;

/// <summary>
/// <c>oadm_docs_search</c> (docs/spec/assistant.md "Questions about OADM"): the user docs of oadm.app that ship with the server
/// (<see cref="DocsIndex"/>). A read of the server's own files: no gRPC method, so every caller may use it.
/// </summary>
internal static class DocsTools
{
    public const string Name = "oadm_docs_search";

    public const string NothingFound = "Nothing found. Try other words.";

    public static IEnumerable<OadmTool> Create(Func<DocsIndex> index)
    {
        ArgumentNullException.ThrowIfNull(index);
        yield return new OadmTool(
            Name,
            "Search the docs",
            "Searches the user docs of OADM for how to do something in OADM. Answers at most 5 pages with the title, the address and the matching passage.",
            Object(null, [P("query", Text("Words of the question, like \"upgrade AXIS OS\".", 200, minLength: 2))], "query"),
            OutObject(null,
            [
                P("hits", OutList("The pages, the best first.", OutObject(null,
                [
                    P("title", Out("string", "The page's title.")),
                    P("url", Out("string", "The page on oadm.app.")),
                    P("passage", Out("string", "The passage that matches best.")),
                ], "title", "url", "passage"))),
                P("note", OrNull(Out("string", "What to do when nothing matched."))),
            ], "hits", "note"),
            [],
            call => Task.FromResult(Search(index(), call.Text("query") ?? string.Empty)));
    }

    private static JsonObject Search(DocsIndex index, string query)
    {
        var hits = new JsonArray();
        var size = 256;
        foreach (var hit in index.Search(query))
        {
            var row = new JsonObject { ["title"] = hit.Title, ["url"] = hit.Url, ["passage"] = hit.Passage };
            var rowSize = ToolAnswers.SizeOf(row) + 1;
            if (size + rowSize > ToolAnswers.MaxBytes)
            {
                break;
            }

            size += rowSize;
            hits.Add(row);
        }

        return new JsonObject { ["hits"] = hits, ["note"] = hits.Count == 0 ? NothingFound : null };
    }
}
