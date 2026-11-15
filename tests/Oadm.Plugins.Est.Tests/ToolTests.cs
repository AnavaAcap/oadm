using System.Text.Json.Nodes;

using Oadm.Plugins.Est.Serving;
using Oadm.Plugins.Est.Tools;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Est.Tests;

/// <summary>The tool of the EST server (docs/spec/mcp.md "Plugins"): its state and the enrollments of the caller's devices, newest first, paged.</summary>
public sealed class ToolTests : IAsyncLifetime
{
    private readonly FakeCoreContext _core = new() { Pro = FakeProLicense.ActivePro() };
    private readonly FakeAuthority _ca = new();
    private readonly EstPlugin _plugin = new();
    private readonly FakeDevice _inside = new() { Serial = "B8A44F631339", Address = "10.0.0.48" };
    private readonly FakeDevice _outside = new() { Serial = "ACCC8E000001", Address = "10.0.0.49" };

    public async Task InitializeAsync()
    {
        _core.Add(_inside);
        _core.Add(_outside);
        _core.FakeServices.Offer<IDeviceCertificateAuthority>(_ca);
        await _core.Settings.SetAsync(Oadm.Plugins.Pki.Servers.ServerSwitch<EstService>.SettingKey, "true", CancellationToken.None);
        await _plugin.StartAsync(_core, CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _plugin.DisposeAsync();
        _ca.Dispose();
    }

    private async Task<JsonNode> ToolAsync(ICallContext call, string? payload = null) =>
        JsonNode.Parse((await _core.InvokeAsync(_plugin, call, EstTools.Enrollments.Method, payload))!)!;

    [Fact]
    public async Task The_enrollments_come_newest_first_a_page_at_a_time_and_a_group_sees_only_its_devices()
    {
        ICorePlugin core = _plugin;
        Assert.Equal("enrollments", Assert.Single(core.Tools).Name);
        Assert.Equal(UserRole.Operator, core.RequiredRole(EstTools.Enrollments.Method));
        Assert.False(core.IsAudited(EstTools.Enrollments.Method));
        var store = _plugin.Service!.Store;
        var now = DateTime.UtcNow;
        store.AddEnrollment(new Enrollment { TimeUtc = now.AddMinutes(-3), DeviceId = _inside.Id, Device = "10.0.0.48", Profile = "https", Ok = true, SerialNumber = "01" }, [1, 2, 3]);
        store.AddEnrollment(new Enrollment { TimeUtc = now.AddMinutes(-2), DeviceId = _outside.Id, Device = "10.0.0.49", Profile = "https", Ok = true, SerialNumber = "02" }, [4, 5, 6]);
        store.AddEnrollment(new Enrollment { TimeUtc = now.AddMinutes(-1), Device = "ACCC8E000099", Profile = "https", Ok = false, Reason = EstRefusals.NotManaged });
        var admin = _core.CallAsAllDevices();

        var first = await ToolAsync(admin, """{"limit":2}""");
        Assert.Equal(3, first["total"]!.GetValue<int>());
        var rows = first["enrollments"]!.AsArray();
        Assert.Equal(["refused", "issued"], rows.Select(r => r!["result"]!.GetValue<string>()));
        Assert.Equal("ACCC8E000099", rows[0]!["device"]!.GetValue<string>());
        var next = first["next"]!.GetValue<string>();
        var second = await ToolAsync(admin, new JsonObject { ["limit"] = 2, ["after"] = next }.ToJsonString());
        Assert.Equal("01", Assert.Single(second["enrollments"]!.AsArray())!["serial_number"]!.GetValue<string>());
        Assert.Null(second["next"]);

        Assert.Equal(1, (await ToolAsync(admin, """{"result":"refused"}"""))["total"]!.GetValue<int>());
        Assert.Equal(1, (await ToolAsync(admin, """{"text":"10.0.0.49"}"""))["total"]!.GetValue<int>());

        var group = await ToolAsync(_core.CallAsGroup([_inside.Id]));
        Assert.Equal(_inside.Id.ToString(), Assert.Single(group["enrollments"]!.AsArray())!["device_id"]!.GetValue<string>());
        Assert.DoesNotContain("ACCC8E000099", group.ToJsonString(), StringComparison.Ordinal);

        var bad = await ToolAsync(admin, """{"result":"maybe"}""");
        Assert.Equal("result", Assert.Single(bad["errors"]!.AsArray())!["field"]!.GetValue<string>());
    }
}
