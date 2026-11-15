using System.Text.RegularExpressions;

using Oadm.Tests.Shared;

namespace Oadm.Server.Tests.Mcp;

/// <summary>
/// Every bundled plugin is readable by agents and the assistant (docs/spec/mcp.md "Plugins", the rule "a new page needs its read
/// tool"): each core plugin declares <c>ICorePlugin.Tools</c>; a package without a core plugin (a task plugin, an event rule
/// action) is covered by the task tools (<c>oadm_tasks_catalog</c>, <c>oadm_task_preview</c>, <c>oadm_tasks_get</c>) and the
/// rule catalog (<c>oadm_rules_catalog</c>); the rest is listed here with its reason. The source is read, not the built
/// assemblies: this project builds only some plugins.
/// </summary>
public sealed partial class McpPluginCoverageTests
{
    private const string OtherBranch = "covered on branch feat/plugin-tools-2";

    /// <summary>The plugins without read tools, each with why. An entry whose plugin declares tools by now fails: drop it.</summary>
    private static readonly Dictionary<string, string> Exceptions = new(StringComparer.Ordinal)
    {
        ["oadm.live-view"] = "live video stays in the client (docs/spec/mcp.md \"Not over MCP\"); one picture is oadm_devices_snapshot",
        ["oadm.web-view"] = "the device's own web interface in the client; OADM keeps nothing of it",
        ["oadm.dashboards"] = OtherBranch,
        ["oadm.edge-recording-export"] = OtherBranch,
        ["oadm.image-health"] = OtherBranch,
        ["oadm.io-monitor"] = OtherBranch,
        ["oadm.metadata-monitor"] = OtherBranch,
        ["oadm.load-test"] = OtherBranch,
        ["oadm.snapshot-report"] = OtherBranch,
        ["oadm.system-report"] = OtherBranch,
        ["oadm.notifications"] = OtherBranch,
        ["oadm.outputs"] = OtherBranch,
        ["oadm.mqtt"] = OtherBranch,
        ["oadm.modbus"] = OtherBranch,
        ["oadm.opcua"] = OtherBranch,
    };

    [GeneratedRegex(@"\b(?:class|record)\s+\w+[^{;]*?:\s*[^{;]*\bICorePlugin\b")]
    private static partial Regex CorePluginClass();

    private static bool Built(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    [Fact]
    public void EveryBundledCorePluginDeclaresReadToolsOrIsListedWithItsReason()
    {
        var manifests = Directory.EnumerateFiles(TestRepository.Path("plugins"), "plugin.json", SearchOption.AllDirectories)
            .Where(f => !Built(f))
            .Select(f => (Id: System.Text.Json.JsonDocument.Parse(File.ReadAllText(f)).RootElement.GetProperty("id").GetString()!, Folder: Path.GetDirectoryName(f)!))
            .ToList();
        Assert.True(manifests.Count > 20, "the scan finds the bundled plugins");
        Assert.All(Exceptions.Keys, id => Assert.Contains(id, manifests.Select(m => m.Id)));
        Assert.All(Exceptions.Values, reason => Assert.False(string.IsNullOrWhiteSpace(reason)));

        var missing = new List<string>();
        foreach (var (id, folder) in manifests)
        {
            // Each file that declares a core plugin (the PKI package holds three) declares its tools too (partial classes:
            // in the same file as the class line).
            var cores = Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
                .Where(f => !Built(f))
                .Select(f => (File: f, Text: File.ReadAllText(f)))
                .Where(f => CorePluginClass().IsMatch(f.Text))
                .ToList();
            var withoutTools = cores.Where(f => !f.Text.Contains("PluginTool> Tools", StringComparison.Ordinal)).Select(f => Path.GetFileName(f.File)).ToList();
            if (Exceptions.TryGetValue(id, out var reason))
            {
                Assert.False(cores.Count > 0 && withoutTools.Count == 0, $"{id} declares its tools now; drop its exception ({reason}).");
                continue;
            }

            if (withoutTools.Count > 0)
            {
                missing.Add($"{id} ({string.Join(", ", withoutTools)})");
            }
        }

        Assert.True(missing.Count == 0, "These core plugins declare no read tools (ICorePlugin.Tools) and are not excepted: " + string.Join("; ", missing));
    }
}
