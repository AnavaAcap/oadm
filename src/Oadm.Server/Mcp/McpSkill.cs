using System.IO.Compression;
using System.Reflection;
using System.Text;

using ModelContextProtocol.Protocol;

namespace Oadm.Server.Mcp;

/// <summary>
/// The skill <c>oadm</c> (docs/spec/mcp.md "Skill"): <c>SKILL.md</c> and the workflow files of <c>Mcp/Skill/</c>, embedded in
/// the server. MCP serves them as the resources <c>oadm://skill/&lt;file&gt;</c>; the Settings card downloads them as a ZIP
/// with the folder <c>oadm/</c>. The files are text: one phrase per concept, no secrets, no device texts.
/// </summary>
public static class McpSkill
{
    /// <summary>The name of the skill and the folder it unpacks into.</summary>
    public const string Name = "oadm";

    /// <summary>The main file.</summary>
    public const string MainFile = "SKILL.md";

    /// <summary>The file name of the download.</summary>
    public const string ZipFileName = "oadm-skill.zip";

    /// <summary>The resource URI of a skill file.</summary>
    public const string UriPrefix = "oadm://skill/";

    /// <summary>The MCP extension that announces a server's skills.</summary>
    public const string Extension = "io.modelcontextprotocol/skills";

    private const string ResourcePrefix = "Oadm.Server.Mcp.Skill.";
    private const string MarkdownType = "text/markdown";

    private static readonly Lazy<Dictionary<string, string>> Loaded = new(Load);

    /// <summary>The files by name: <c>SKILL.md</c> first, then the workflows in name order.</summary>
    public static IReadOnlyDictionary<string, string> Files => Loaded.Value;

    /// <summary>The URI of <paramref name="file"/>.</summary>
    public static string UriOf(string file) => UriPrefix + file;

    /// <summary>The answer of <c>resources/list</c>.</summary>
    internal static ListResourcesResult List() => new()
    {
        Resources = [.. Files.Keys.Select(file => new Resource
        {
            Uri = UriOf(file),
            Name = file,
            Title = file == MainFile ? "OADM skill" : "OADM skill: " + file,
            Description = file == MainFile ? "How an agent works with OADM." : "A workflow of the OADM skill.",
            MimeType = MarkdownType,
        })],
    };

    /// <summary>The answer of <c>resources/read</c>; an unknown URI is a protocol error (-32602).</summary>
    internal static ReadResourceResult Read(string? uri)
    {
        if (uri is null || !uri.StartsWith(UriPrefix, StringComparison.Ordinal) || !Files.TryGetValue(uri[UriPrefix.Length..], out var text))
        {
            throw new ModelContextProtocol.McpProtocolException("Unknown resource.", ModelContextProtocol.McpErrorCode.InvalidParams);
        }

        return new ReadResourceResult { Contents = [new TextResourceContents { Uri = uri, MimeType = MarkdownType, Text = text }] };
    }

    /// <summary>The skill as a ZIP (<c>oadm/SKILL.md</c> and the workflow files), the same bytes every time.</summary>
    public static byte[] Zip()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (file, text) in Files)
            {
                var entry = zip.CreateEntry(Name + "/" + file, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var content = entry.Open();
                content.Write(Encoding.UTF8.GetBytes(text));
            }
        }

        return stream.ToArray();
    }

    private static Dictionary<string, string> Load()
    {
        var assembly = typeof(McpSkill).Assembly;
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            files[resource[ResourcePrefix.Length..]] = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
        }

        var ordered = new Dictionary<string, string>();
        if (files.Remove(MainFile, out var main))
        {
            ordered[MainFile] = main;
        }

        foreach (var (file, text) in files)
        {
            ordered[file] = text;
        }

        return ordered;
    }
}
