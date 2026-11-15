using System.Text.Json.Nodes;

using Oadm.Plugins.Radius.Api;
using Oadm.Plugins.Radius.Tests.Support;
using Oadm.Plugins.Radius.Tools;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Radius.Tests;

/// <summary>The page's Sessions tab and Delete log as tools (docs/spec/mcp.md "Plugins", decision 2026-11-15), with the rights of their page methods.</summary>
public sealed class PageToolTests
{
    [Fact]
    public void The_tools_have_the_rights_of_their_page_methods()
    {
        ICorePlugin core = new RadiusPlugin();
        Assert.Equal(core.RequiredRole(RadiusMethods.ListSessions), core.RequiredRole("tool.sessions"));
        Assert.Equal(UserRole.Operator, core.RequiredRole("tool.sessions"));
        Assert.Equal(UserRole.Admin, core.RequiredRole("tool.log_delete"));
        Assert.Equal(UserRole.Admin, core.RequiredRole("tool.log_delete.preview"));
        Assert.True(core.RequiresAllDevices("tool.log_delete"));
        Assert.True(core.RequiresAllDevices("tool.log_delete.preview"));
        Assert.False(core.RequiresAllDevices("tool.sessions"));
        Assert.True(core.IsAudited("tool.log_delete"));
        Assert.False(core.IsAudited("tool.log_delete.preview"));
        Assert.False(core.IsAudited("tool.sessions"));
        Assert.True(RadiusPageTools.Set.ToolOf("tool.log_delete")!.Destructive);
    }

    [Fact]
    public async Task Sessions_show_the_callers_devices_and_Delete_log_previews_then_runs_once()
    {
        await using var harness = await PluginHarness.CreateAsync(start: false);
        var inside = harness.AddDevice("ACCC8E000001");
        var outside = harness.AddDevice("ACCC8E000002");
        await harness.StartAsync();
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        foreach (var certificate in new[] { harness.Ca.Camera(inside.Serial), harness.Ca.Camera(outside.Serial) })
        {
            await harness.Switch().RunAsync(new TestSupplicant(new SupplicantOptions { Certificate = certificate, TrustedCa = harness.Ca.CaCertificate }));
        }

        harness.Tick();
        async Task<JsonNode> CallAsync(ICallContext call, string method, string? payload = null) =>
            JsonNode.Parse((await harness.Ctx.InvokeAsync(harness.Plugin, call, method, payload))!)!;

        var all = await CallAsync(harness.Admin, "tool.sessions");
        Assert.True(all["running"]!.GetValue<bool>());
        Assert.Equal(2, all["total"]!.GetValue<int>());
        var first = await CallAsync(harness.Admin, "tool.sessions", """{"limit":1}""");
        Assert.Single(first["sessions"]!.AsArray());
        var second = await CallAsync(harness.Admin, "tool.sessions", new JsonObject { ["limit"] = 1, ["after"] = first["next"]!.GetValue<string>() }.ToJsonString());
        Assert.Null(second["next"]);
        var mine = await CallAsync(harness.Ctx.CallAsGroup([inside.Id]), "tool.sessions");
        Assert.Equal(inside.Id.ToString(), Assert.Single(mine["sessions"]!.AsArray())!["device_id"]!.GetValue<string>());

        var entries = harness.Plugin.Service!.Log.Count;
        Assert.True(entries >= 2);
        var plan = await CallAsync(harness.Admin, "tool.log_delete.preview");
        Assert.Equal(entries, plan["will_change"]!.GetValue<int>());
        Assert.Equal("log line", plan["unit"]!.GetValue<string>());
        Assert.Equal(entries, harness.Plugin.Service!.Log.Count); // the preview deletes nothing

        var done = await CallAsync(harness.Admin, "tool.log_delete");
        Assert.Equal(entries, done["deleted"]!.GetValue<int>());
        Assert.Equal(0, harness.Plugin.Service!.Log.Count);
        var again = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Ctx.InvokeAsync(harness.Plugin, harness.Admin, "tool.log_delete", null));
        Assert.Equal(RadiusPageTools.EmptyLog, again.Message);
    }

    [Fact]
    public async Task While_the_server_is_off_there_are_no_sessions_and_nothing_to_delete()
    {
        await using var harness = await PluginHarness.CreateAsync(h => h.TurnedOn = false);
        var answer = JsonNode.Parse((await harness.Ctx.InvokeAsync(harness.Plugin, harness.Admin, "tool.sessions", null))!)!;
        Assert.False(answer["running"]!.GetValue<bool>());
        Assert.Empty(answer["sessions"]!.AsArray());
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Ctx.InvokeAsync(harness.Plugin, harness.Admin, "tool.log_delete.preview", null));
    }

    private static async Task<JsonNode> CallAsync(PluginHarness harness, string method, JsonObject? payload = null, ICallContext? caller = null) =>
        JsonNode.Parse((await harness.Ctx.InvokeAsync(harness.Plugin, caller ?? harness.Admin, method, payload?.ToJsonString()))!)!;

    private static string FieldOf(JsonNode answer) => Assert.Single(answer["errors"]!.AsArray())!["field"]!.GetValue<string>();

    [Fact]
    public void The_server_tools_have_the_rights_of_the_page_and_audit_its_page_method()
    {
        ICorePlugin core = new RadiusPlugin();
        foreach (var name in new[] { "enable", "settings_save", "switch_save", "switch_delete" })
        {
            Assert.Equal(UserRole.Admin, core.RequiredRole("tool." + name));
            Assert.Equal(UserRole.Admin, core.RequiredRole("tool." + name + ".preview"));
            Assert.True(core.RequiresAllDevices("tool." + name + ".preview"));
            Assert.True(core.IsAudited("tool." + name));
            Assert.False(core.IsAudited("tool." + name + ".preview"));
        }

        Assert.Equal(UserRole.Operator, core.RequiredRole("tool.switches"));
        Assert.Equal("turnOff", core.AuditDetail("tool.enable", """{"enabled":false}"""));
        Assert.Equal("turnOn", core.AuditDetail("tool.enable", """{"enabled":true}"""));
        Assert.Equal(RadiusMethods.SaveSwitch, core.AuditDetail("tool.switch_save", "{}"));
        Assert.Null(core.AuditDetail("tool.switch_save.preview", "{}"));
        Assert.Equal(["shared_secret"], RadiusPageTools.Set.ToolOf("tool.switch_save")!.Secrets.Select(s => s.Name));
        Assert.True(core.RequiresAllDevices(Oadm.Plugins.Pki.Tools.ToolFileStore.Method));
    }

    [Fact]
    public async Task A_switch_is_added_with_the_entered_secret_only_by_the_run_and_never_shows_it()
    {
        await using var harness = await PluginHarness.CreateAsync();
        var plan = await CallAsync(harness, "tool.switch_save.preview", new JsonObject { ["name"] = "Core", ["address"] = "10.0.0.2" });
        Assert.Equal(["shared_secret"], plan["secrets_needed"]!.AsArray().Select(n => n!["name"]!.GetValue<string>()));
        Assert.Equal(("create", "enter"), (plan["change"]!.GetValue<string>(), plan["secret"]!.GetValue<string>()));
        Assert.Empty(harness.Service.ListSwitches().Switches); // nothing stored
        Assert.Equal("secret", FieldOf(await CallAsync(harness, "tool.switch_save.preview", new JsonObject { ["name"] = "Core", ["address"] = "10.0.0.2", ["secret"] = "keep" })));
        Assert.Equal("address", FieldOf(await CallAsync(harness, "tool.switch_save.preview", new JsonObject { ["name"] = "Core", ["address"] = "not an address!" })));

        var tooShort = await CallAsync(harness, "tool.switch_save", new JsonObject { ["name"] = "Core", ["address"] = "10.0.0.2", ["secrets"] = new JsonObject { ["shared_secret"] = "short" } });
        Assert.Equal("secrets.shared_secret", FieldOf(tooShort));
        Assert.Empty(harness.Service.ListSwitches().Switches);

        var done = await CallAsync(harness, "tool.switch_save", new JsonObject { ["name"] = "Core", ["address"] = "10.0.0.2", ["secrets"] = new JsonObject { ["shared_secret"] = "canary-switch-secret-1" } });
        Assert.DoesNotContain("canary-switch-secret-1", done.ToJsonString(), StringComparison.Ordinal);
        Assert.Null(done["file"]);
        var row = Assert.Single(harness.Service.ListSwitches().Switches);
        Assert.Equal(row.Id.ToString(), done["id"]!.GetValue<string>());
        var listed = await CallAsync(harness, "tool.switches");
        Assert.DoesNotContain("canary-switch-secret-1", listed.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal("Core", Assert.Single(listed["switches"]!.AsArray())!["name"]!.GetValue<string>());

        // A rename keeps the secret: no secret needed.
        var rename = await CallAsync(harness, "tool.switch_save.preview", new JsonObject { ["switch_id"] = row.Id.ToString(), ["name"] = "Core 1" });
        Assert.Empty(rename["secrets_needed"]!.AsArray());
        await CallAsync(harness, "tool.switch_save", new JsonObject { ["switch_id"] = row.Id.ToString(), ["name"] = "Core 1" });
        Assert.Equal("Core 1", Assert.Single(harness.Service.ListSwitches().Switches).Name);

        var delete = await CallAsync(harness, "tool.switch_delete.preview", new JsonObject { ["switch_id"] = row.Id.ToString() });
        Assert.Equal("Core 1", delete["name"]!.GetValue<string>());
        Assert.Single(harness.Service.ListSwitches().Switches);
        await CallAsync(harness, "tool.switch_delete", new JsonObject { ["switch_id"] = row.Id.ToString() });
        Assert.Empty(harness.Service.ListSwitches().Switches);
        Assert.Equal("switch_id", FieldOf(await CallAsync(harness, "tool.switch_delete.preview", new JsonObject { ["switch_id"] = row.Id.ToString() })));
    }

    [Fact]
    public async Task A_generated_secret_is_handed_out_once_as_a_file_to_its_caller_only()
    {
        await using var harness = await PluginHarness.CreateAsync();
        var row = await harness.AddSwitchAsync("Core", "10.0.0.2");
        var plan = await CallAsync(harness, "tool.switch_save.preview", new JsonObject { ["switch_id"] = row.Id.ToString(), ["secret"] = "generate" });
        Assert.Empty(plan["secrets_needed"]!.AsArray());
        Assert.Contains(plan["warnings"]!.AsArray(), w => w!.GetValue<string>().Contains("on the switch too", StringComparison.Ordinal));

        var done = await CallAsync(harness, "tool.switch_save", new JsonObject { ["switch_id"] = row.Id.ToString(), ["secret"] = "generate" });
        var file = done["file"]!;
        var payload = file["payload_json"]!.GetValue<string>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Plugin.OpenDownloadAsync(harness.Ctx.CallAsAllDevices("other"), file["method"]!.GetValue<string>(), payload, CancellationToken.None));
        string text;
        await using (var download = await harness.Plugin.OpenDownloadAsync(harness.Admin, file["method"]!.GetValue<string>(), payload, CancellationToken.None))
        {
            using var reader = new StreamReader(download.Content);
            text = await reader.ReadToEndAsync();
        }

        var secret = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[^1];
        Assert.Equal(24, secret.Length);
        Assert.DoesNotContain(secret, done.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain(PluginHarness.Secret, text, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Plugin.OpenDownloadAsync(harness.Admin, file["method"]!.GetValue<string>(), payload, CancellationToken.None)); // once
    }

    [Fact]
    public async Task Turn_off_and_on_and_the_settings_change_only_with_the_run()
    {
        await using var harness = await PluginHarness.CreateAsync();
        var kept = harness.Service.Settings.LogKept;
        var plan = await CallAsync(harness, "tool.settings_save.preview", new JsonObject { ["log_kept"] = 2000 });
        Assert.Equal(2000, plan["log_kept"]!.GetValue<int>());
        Assert.Equal(kept, harness.Service.Settings.LogKept);
        Assert.Equal("port", FieldOf(await CallAsync(harness, "tool.settings_save.preview", new JsonObject { ["port"] = 0 })));
        Assert.Equal("listen_on", FieldOf(await CallAsync(harness, "tool.settings_save.preview", new JsonObject { ["listen_on"] = "gone" })));
        await CallAsync(harness, "tool.settings_save", new JsonObject { ["log_kept"] = 2000 });
        Assert.Equal(2000, harness.Service.Settings.LogKept);

        var off = await CallAsync(harness, "tool.enable.preview", new JsonObject { ["enabled"] = false });
        Assert.False(off["enabled"]!.GetValue<bool>());
        Assert.True(harness.Plugin.Server!.On); // nothing changed
        var done = await CallAsync(harness, "tool.enable", new JsonObject { ["enabled"] = false });
        Assert.False(done["on"]!.GetValue<bool>());
        Assert.Null(harness.Plugin.Service);
        var again = await Assert.ThrowsAsync<InvalidOperationException>(() => CallAsync(harness, "tool.enable.preview", new JsonObject { ["enabled"] = false }));
        Assert.Equal(RadiusPageTools.AlreadyOff, again.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CallAsync(harness, "tool.switch_save.preview", new JsonObject { ["name"] = "Core", ["address"] = "10.0.0.2" }));

        await CallAsync(harness, "tool.enable", new JsonObject { ["enabled"] = true });
        Assert.True(harness.Plugin.Server!.On);
        harness.Pro.Current = Oadm.Sdk.Licensing.ProLicenseInfo.None;
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => CallAsync(harness, "tool.enable.preview", new JsonObject { ["enabled"] = true }));
        Assert.Equal(Oadm.Sdk.Licensing.ProLicenseTexts.NeedsPro, refused.Message);
    }
}
