using System.Text.Json.Nodes;

using Oadm.Plugins.Est.Serving;
using Oadm.Plugins.Est.Tools;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Est.Tests;

/// <summary>Delete all of the Enrollments tab as a tool (docs/spec/mcp.md "Plugins", decision 2026-11-15), with the rights of its page method.</summary>
public sealed class PageToolTests : IAsyncLifetime
{
    private readonly FakeCoreContext _core = new() { Pro = FakeProLicense.ActivePro() };
    private readonly FakeAuthority _ca = new();
    private readonly EstPlugin _plugin = new();

    public async Task InitializeAsync()
    {
        _core.FakeServices.Offer<IDeviceCertificateAuthority>(_ca);
        await _core.Settings.SetAsync(Oadm.Plugins.Pki.Servers.ServerSwitch<EstService>.SettingKey, "true", CancellationToken.None);
        await _plugin.StartAsync(_core, CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _plugin.DisposeAsync();
        _ca.Dispose();
    }

    [Fact]
    public void The_tool_has_the_rights_of_Delete_all()
    {
        ICorePlugin core = _plugin;
        foreach (var method in new[] { "tool.enrollments_clear", "tool.enrollments_clear.preview" })
        {
            Assert.Equal(core.RequiredRole(EstMethods.ClearEnrollments), core.RequiredRole(method));
            Assert.Equal(UserRole.Admin, core.RequiredRole(method));
            Assert.True(core.RequiresAllDevices(method));
        }

        Assert.True(core.IsAudited("tool.enrollments_clear"));
        Assert.False(core.IsAudited("tool.enrollments_clear.preview"));
        Assert.True(EstPageTools.Set.ToolOf("tool.enrollments_clear")!.Destructive);
    }

    [Fact]
    public async Task The_preview_deletes_nothing_the_run_deletes_all_once_and_a_group_is_refused()
    {
        var store = _plugin.Service!.Store;
        store.AddEnrollment(new Enrollment { TimeUtc = DateTime.UtcNow, Device = "ACCC8E000099", Profile = "https", Ok = false, Reason = EstRefusals.NotManaged });
        store.AddEnrollment(new Enrollment { TimeUtc = DateTime.UtcNow, Device = "ACCC8E000098", Profile = "https", Ok = false, Reason = EstRefusals.NotManaged });
        var admin = _core.CallAsAllDevices();

        var plan = JsonNode.Parse((await _core.InvokeAsync(_plugin, admin, "tool.enrollments_clear.preview", null))!)!;
        Assert.Equal(2, plan["will_change"]!.GetValue<int>());
        Assert.Equal("enrollment", plan["unit"]!.GetValue<string>());
        Assert.Equal(2, store.Enrollments().Count);

        await Assert.ThrowsAnyAsync<Exception>(() => _core.InvokeAsync(_plugin, _core.CallAsGroup([]), "tool.enrollments_clear", null));
        Assert.Equal(2, store.Enrollments().Count);

        var done = JsonNode.Parse((await _core.InvokeAsync(_plugin, admin, "tool.enrollments_clear", null))!)!;
        Assert.Equal(2, done["deleted"]!.GetValue<int>());
        Assert.Empty(store.Enrollments());
        var again = await Assert.ThrowsAsync<InvalidOperationException>(() => _core.InvokeAsync(_plugin, admin, "tool.enrollments_clear", null));
        Assert.Equal(EstPageTools.NoEnrollments, again.Message);
    }

    private async Task<JsonNode> CallAsync(string method, JsonObject? payload = null) =>
        JsonNode.Parse((await _core.InvokeAsync(_plugin, _core.CallAsAllDevices(), method, payload?.ToJsonString()))!)!;

    private static string FieldOf(JsonNode answer) => Assert.Single(answer["errors"]!.AsArray())!["field"]!.GetValue<string>();

    [Fact]
    public void The_server_tools_have_the_rights_of_the_page_and_audit_its_page_method()
    {
        ICorePlugin core = _plugin;
        foreach (var name in new[] { "enable", "settings_save", "profile_save", "profile_delete" })
        {
            Assert.Equal(UserRole.Admin, core.RequiredRole("tool." + name + ".preview"));
            Assert.True(core.RequiresAllDevices("tool." + name + ".preview"));
            Assert.True(core.IsAudited("tool." + name));
            Assert.False(core.IsAudited("tool." + name + ".preview"));
        }

        Assert.Equal("turnOff", core.AuditDetail("tool.enable", """{"enabled":false}"""));
        Assert.Equal("turnOn", core.AuditDetail("tool.enable", """{"enabled":true}"""));
        Assert.Equal(EstMethods.Save, core.AuditDetail("tool.profile_save", "{}"));
        Assert.True(EstPageTools.Set.ToolOf("tool.profile_delete")!.Destructive);
    }

    [Fact]
    public async Task A_profile_and_the_settings_change_only_with_the_run_with_the_pages_checks()
    {
        var store = _plugin.Service!.Store;
        var plan = await CallAsync("tool.profile_save.preview", new JsonObject { ["name"] = "mqtt", ["use"] = "mqtt" });
        Assert.Equal(("create", "mqtt", "profile"), (plan["change"]!.GetValue<string>(), plan["use"]!.GetValue<string>(), plan["unit"]!.GetValue<string>()));
        Assert.DoesNotContain(store.Config.Profiles, p => p.Name == "mqtt");
        Assert.Equal("use", FieldOf(await CallAsync("tool.profile_save.preview", new JsonObject { ["name"] = "web", ["use"] = "https" }))); // one profile per use
        Assert.Equal("name", FieldOf(await CallAsync("tool.profile_save.preview", new JsonObject { ["name"] = "bad-name", ["use"] = "rtsps" })));
        Assert.Equal("use", FieldOf(await CallAsync("tool.profile_save.preview", new JsonObject { ["name"] = "x" })));

        await CallAsync("tool.profile_save", new JsonObject { ["name"] = "mqtt", ["use"] = "mqtt" });
        Assert.Contains(store.Config.Profiles, p => p.Name == "mqtt" && p.Use == ProfileUse.Mqtt);

        var delete = await CallAsync("tool.profile_delete.preview", new JsonObject { ["name"] = "MQTT" });
        Assert.NotEmpty(delete["warnings"]!.AsArray());
        Assert.Contains(store.Config.Profiles, p => p.Name == "mqtt");
        await CallAsync("tool.profile_delete", new JsonObject { ["name"] = "mqtt" });
        Assert.DoesNotContain(store.Config.Profiles, p => p.Name == "mqtt");
        Assert.Equal("name", FieldOf(await CallAsync("tool.profile_delete.preview", new JsonObject { ["name"] = "mqtt" })));

        var kept = store.Config.EnrollmentsKept;
        var settings = await CallAsync("tool.settings_save.preview", new JsonObject { ["enrollments_kept"] = 200 });
        Assert.Equal(200, settings["enrollments_kept"]!.GetValue<int>());
        Assert.Equal(kept, store.Config.EnrollmentsKept);
        Assert.Equal("port", FieldOf(await CallAsync("tool.settings_save.preview", new JsonObject { ["port"] = 80 })));
        Assert.Equal("interface_id", FieldOf(await CallAsync("tool.settings_save.preview", new JsonObject { ["interface_id"] = "gone" })));
        await CallAsync("tool.settings_save", new JsonObject { ["enrollments_kept"] = 200 });
        Assert.Equal(200, store.Config.EnrollmentsKept);
    }

    [Fact]
    public async Task Turn_off_and_on_change_the_server_only_with_the_run_and_need_oadm_pro()
    {
        var plan = await CallAsync("tool.enable.preview", new JsonObject { ["enabled"] = false });
        Assert.NotEmpty(plan["warnings"]!.AsArray());
        Assert.True(_plugin.Server!.On);
        await CallAsync("tool.enable", new JsonObject { ["enabled"] = false });
        Assert.False(_plugin.Server!.On);
        Assert.Null(_plugin.Service);
        Assert.Equal(EstPageTools.AlreadyOff, (await Assert.ThrowsAsync<InvalidOperationException>(() => CallAsync("tool.enable.preview", new JsonObject { ["enabled"] = false }))).Message);
        Assert.Equal(EstPageTools.Off, (await Assert.ThrowsAsync<InvalidOperationException>(() => CallAsync("tool.profile_save.preview", new JsonObject { ["name"] = "mqtt", ["use"] = "mqtt" }))).Message);

        await CallAsync("tool.enable", new JsonObject { ["enabled"] = true });
        Assert.True(_plugin.Server!.On);
        ((FakeProLicense)_core.Pro!).Current = Oadm.Sdk.Licensing.ProLicenseInfo.None;
        Assert.Equal(Oadm.Sdk.Licensing.ProLicenseTexts.NeedsPro,
            (await Assert.ThrowsAsync<InvalidOperationException>(() => CallAsync("tool.enable.preview", new JsonObject { ["enabled"] = true }))).Message);
    }
}
