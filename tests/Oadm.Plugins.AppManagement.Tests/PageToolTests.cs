using System.Net;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Acap;
using Oadm.Plugins.AppManagement.Reading;
using Oadm.Plugins.AppManagement.Tools;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.AppManagement.Tests;

/// <summary>
/// The page's reads as tools (docs/spec/mcp.md "Plugins", decision 2026-11-15): Read devices, Show log and the Settings read with
/// the rights of their page methods, the caller's devices only, no secret value, nothing written to a device.
/// </summary>
public sealed class PageToolTests : IAsyncLifetime
{
    private readonly FakeCoreContext _ctx = new();
    private AppManagementPlugin _plugin = null!;

    public async Task InitializeAsync()
    {
        _plugin = new AppManagementPlugin(new AppManagementOptions { Time = new FakeTimeProvider(), EventInterval = TimeSpan.FromHours(1) });
        await _plugin.StartAsync(_ctx, CancellationToken.None);
    }

    public async Task DisposeAsync() => await _plugin.DisposeAsync();

    private static readonly InstalledApplication Vmd = Samples.App("vmd", "AXIS Video Motion Detection", "4.5.2", bundled: true);

    private static string Ids(params Guid[] ids) => new JsonArray([.. ids.Select(i => (JsonNode)i.ToString())]).ToJsonString();

    private async Task<JsonNode> ToolAsync(ICallContext call, string method, string payload) =>
        JsonNode.Parse((await _ctx.InvokeAsync(_plugin, call, method, payload))!)!;

    /// <summary>A camera that answers the log and the settings of "demo" and refuses everything else.</summary>
    private static FakeCamera LogAndSettings() => new()
    {
        Respond = (request, _) =>
        {
            var uri = request.RequestUri?.OriginalString ?? string.Empty;
            return uri.StartsWith(AppLogReader.LogPath, StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(LogAndSettingsTests.Log) }
                : uri.Contains("listdefinitions", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(LogAndSettingsTests.Typed()) }
                : new HttpResponseMessage(HttpStatusCode.BadRequest);
        },
    };

    [Fact]
    public void Every_tool_has_the_rights_of_its_page_method_and_none_changes()
    {
        ICorePlugin core = _plugin;
        foreach (var (tool, page) in new[] { ("tool.read", AppManagementMethods.Read), ("tool.log", AppManagementMethods.GetLog), ("tool.settings", AppManagementMethods.GetSettings) })
        {
            Assert.Equal(page, AppPageTools.Set.PageMethodOf(tool));
            Assert.Equal(core.RequiredPermission(page), core.RequiredPermission(tool));
            Assert.Equal(core.RequiredRole(page), core.RequiredRole(tool));
            Assert.Equal(core.RequiresAllDevices(page), core.RequiresAllDevices(tool));
            Assert.False(core.IsAudited(tool));
        }

        Assert.All(AppPageTools.Set.Tools, t => Assert.False(t.Changes));
        Assert.DoesNotContain(core.Tools, t => t.Name.Contains("install", StringComparison.Ordinal) || t.Name.Contains("run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Read_fills_the_callers_session_which_the_applications_tool_answers()
    {
        var a = _ctx.Add(Samples.ManagedDevice("10.0.0.1"), Samples.Camera(Vmd));
        var b = _ctx.Add(Samples.ManagedDevice("10.0.0.2"), Samples.Camera(Vmd));
        var call = _ctx.CallAsAllDevices();

        var started = await ToolAsync(call, "tool.read", $$"""{"device_ids":{{Ids(a.Id)}},"devices_total":1}""");
        Assert.Equal(1, started["total"]!.GetValue<int>());
        await Eventually.TrueAsync(async () => !AppManagementJson.Deserialize<ReadState>(await _ctx.InvokeAsync(_plugin, call, AppManagementMethods.GetState, null)).Reading, "the read ended");
        var apps = await ToolAsync(call, AppTools.Applications.Method, "{}");
        Assert.Equal(1, apps["devices"]!.GetValue<int>());
        Assert.Empty(_ctx.Cameras[b.Id].Requests);

        var all = await ToolAsync(call, "tool.read", """{"all_devices":true}""");
        Assert.Equal(2, all["total"]!.GetValue<int>());
        await Eventually.TrueAsync(async () => !AppManagementJson.Deserialize<ReadState>(await _ctx.InvokeAsync(_plugin, call, AppManagementMethods.GetState, null)).Reading, "the second read ended");
        Assert.All(_ctx.Cameras.Values, c => Assert.Equal(0, c.Writes));
    }

    [Fact]
    public async Task The_log_is_the_newest_lines_and_invalid_input_gives_field_errors()
    {
        var device = _ctx.Add(Samples.ManagedDevice("10.0.0.48"), LogAndSettings());
        var call = _ctx.CallAsAllDevices();

        var log = await ToolAsync(call, "tool.log", $$"""{"device_id":"{{device.Id}}","application":"demo","lines":2}""");
        Assert.Equal(3, log["total"]!.GetValue<int>());
        var lines = log["lines"]!.AsArray();
        Assert.Equal(2, lines.Count);
        Assert.EndsWith("Could not reach 10.0.0.5", lines[^1]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("ERR", lines[^1]!["level"]!.GetValue<string>());
        Assert.True(log["truncated"]!.GetValue<bool>());

        var badApp = await ToolAsync(call, "tool.log", $$"""{"device_id":"{{device.Id}}","application":"../x"}""");
        Assert.Equal(AppPageTools.NotValidApplication, badApp["errors"]![0]!["error"]!.GetValue<string>());
        var badId = await ToolAsync(call, "tool.log", """{"device_id":"lobby","application":"demo"}""");
        Assert.Equal("device_id", badId["errors"]![0]!["field"]!.GetValue<string>());
        var noDevices = await ToolAsync(call, "tool.settings", """{"application":"demo","device_ids":[],"devices_total":0}""");
        Assert.Equal(AppPageTools.ChooseDevices, noDevices["errors"]![0]!["error"]!.GetValue<string>());
        var noAll = await ToolAsync(call, "tool.read", """{"device_ids":[],"devices_total":0}""");
        Assert.Equal("filter", noAll["errors"]![0]!["field"]!.GetValue<string>());
        Assert.Equal(0, _ctx.Cameras[device.Id].Writes);
    }

    [Fact]
    public async Task Settings_are_grouped_by_value_and_never_show_a_password_or_a_key()
    {
        var a = _ctx.Add(Samples.ManagedDevice("10.0.0.48"), LogAndSettings());
        var b = _ctx.Add(Samples.ManagedDevice("10.0.0.49"), LogAndSettings());
        var call = _ctx.CallAsAllDevices();

        var answer = await ToolAsync(call, "tool.settings", $$"""{"application":"demo","device_ids":{{Ids(a.Id, b.Id)}},"devices_total":2}""");
        Assert.Equal(2, answer["read"]!.GetValue<int>());
        Assert.True(answer["values_shown"]!.GetValue<bool>());
        var settings = answer["settings"]!.AsArray();
        Assert.DoesNotContain(settings, s => s!["name"]!.GetValue<string>() == "Internal"); // hidden
        var threshold = settings.Single(s => s!["name"]!.GetValue<string>() == "Threshold")!;
        Assert.Equal("40", threshold["values"]![0]!["value"]!.GetValue<string>());
        Assert.Equal(2, threshold["values"]![0]!["devices"]!.GetValue<int>());
        var key = settings.Single(s => s!["name"]!.GetValue<string>() == "Key")!;
        Assert.False(key["value_shown"]!.GetValue<bool>());
        Assert.Empty(key["values"]!.AsArray());
        Assert.DoesNotContain("******", answer.ToJsonString(), StringComparison.Ordinal);

        // Without the right to change settings: the page's rule, no values.
        var denied = new DeniedCall(call, PermissionKeys.Task(AcapPlugin.Id, AcapPermissions.Settings));
        var hidden = JsonNode.Parse((await _plugin.InvokeAsync(denied, "tool.settings", $$"""{"application":"demo","device_ids":{{Ids(a.Id)}},"devices_total":1}""", CancellationToken.None))!)!;
        Assert.False(hidden["values_shown"]!.GetValue<bool>());
        Assert.All(hidden["settings"]!.AsArray(), s => Assert.Empty(s!["values"]!.AsArray()));
    }

    [Fact]
    public async Task A_group_reads_only_its_own_devices()
    {
        var mine = _ctx.Add(Samples.ManagedDevice("10.0.0.48"), LogAndSettings());
        var other = _ctx.Add(Samples.ManagedDevice("10.0.0.49"), LogAndSettings());
        var call = _ctx.CallAsGroup([mine.Id]);

        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => ToolAsync(call, "tool.log", $$"""{"device_id":"{{other.Id}}","application":"demo"}"""));
        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => ToolAsync(call, "tool.settings", $$"""{"application":"demo","device_ids":{{Ids(mine.Id, other.Id)}},"devices_total":2}"""));
        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => ToolAsync(call, "tool.read", $$"""{"device_ids":{{Ids(other.Id)}},"devices_total":1}"""));
        Assert.Empty(_ctx.Cameras[other.Id].Requests);

        var all = await ToolAsync(call, "tool.read", """{"all_devices":true}""");
        Assert.Equal(1, all["total"]!.GetValue<int>());
        await Eventually.TrueAsync(async () => !AppManagementJson.Deserialize<ReadState>(await _ctx.InvokeAsync(_plugin, call, AppManagementMethods.GetState, null)).Reading, "the read ended");
        Assert.Empty(_ctx.Cameras[other.Id].Requests);
    }
}
