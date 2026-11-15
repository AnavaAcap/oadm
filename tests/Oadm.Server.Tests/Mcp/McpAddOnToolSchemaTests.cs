extern alias ocr;
extern alias vision;

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Oadm.Sdk.Plugins;
using Oadm.Server.Tasks;

namespace Oadm.Server.Tests.Mcp;

/// <summary>
/// The tools of the optional plugins Text recognition and Vision Center (docs/spec/mcp.md "Plugins"): the host's schema rules hold
/// for every tool, so the host offers them all. The plugins are not started here (no model, no graphics card); their own tests
/// call the tools.
/// </summary>
public sealed partial class McpAddOnToolSchemaTests
{
    [GeneratedRegex("^[a-z][a-z0-9]*(_[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();

    public static TheoryData<string> AddOns() => ["ocr", "vision"];

    private static ICorePlugin PluginOf(string name) => name switch
    {
        "ocr" => new ocr::Oadm.Plugins.Ocr.OcrPlugin(),
        _ => new vision::Oadm.Plugins.Vision.VisionPlugin(),
    };

    [Theory]
    [MemberData(nameof(AddOns))]
    public void EveryToolOfTheOptionalPluginHasAStrictInputAndAnObjectAnswer(string name)
    {
        var plugin = PluginOf(name);
        Assert.Equal(name, plugin.ToolArea ?? plugin.Id["oadm.".Length..]);
        var names = new HashSet<string>(StringComparer.Ordinal);
        Assert.NotEmpty(plugin.Tools);
        foreach (var tool in plugin.Tools)
        {
            Assert.True(NameRegex().IsMatch(tool.Name) && tool.Name.Length <= PluginTool.MaxNameLength, tool.Name);
            Assert.True(names.Add(tool.Name), tool.Name);
            Assert.False(!tool.Changes && (tool.Name.EndsWith("_preview", StringComparison.Ordinal) || tool.Name.EndsWith("_run", StringComparison.Ordinal)), tool.Name);
            var input = TaskPayloadSchemas.Build(tool.InputType!, tool.JsonOptions);
            Assert.True(input is not null, $"{tool.Name}: the input has no schema the host offers");
            Assert.True(input!["additionalProperties"] is JsonValue flag && !flag.GetValue<bool>(), $"{tool.Name}: the input takes other properties");
            Assert.True(TaskPayloadSchemas.Build(tool.OutputType!, tool.JsonOptions) is not null, $"{tool.Name}: the answer has no schema");
            if (tool.Changes)
            {
                Assert.True(TaskPayloadSchemas.Build(tool.RunOutputType!, tool.JsonOptions) is not null, $"{tool.Name}: the run's answer has no schema");
                var properties = TaskPayloadSchemas.Build(tool.OutputType!, tool.JsonOptions)!["properties"]!.AsObject();
                Assert.False(properties.ContainsKey("handle") || properties.ContainsKey("expires"), tool.Name);
            }

            if (tool.Paged)
            {
                Assert.True(TaskPayloadSchemas.Build(tool.OutputType!, tool.JsonOptions)!["properties"]!.AsObject().ContainsKey("next"), tool.Name);
            }
        }

        // The page tools of the optional plugins: their changes and reads, beside the earlier reads.
        var expected = name == "ocr"
            ? new[] { "readers", "reads", "reader_save", "reader_delete", "reader_read_now", "use_gpu" }
            : ["pipelines", "pipeline", "pipeline_save", "pipeline_copy", "pipeline_delete", "models", "class_lists", "class_list_save", "model_upload", "model_update", "model_delete", "use_gpu"];
        Assert.All(expected, e => Assert.Contains(e, names));
    }
}
