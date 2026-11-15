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
}
