using Oadm.Plugins.DateAndTime.Client;
using Oadm.Plugins.DateAndTime.Model;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.DateAndTime.Tests;

/// <summary>
/// The server runs <see cref="DateTimeTaskPlugin.ValidatePayload"/> before any task of any client starts, so every payload the
/// "Set date and time" dialog can send must pass it. The dialog's view model is driven through OK and the exact JSON it
/// completes with is handed to the validator.
/// </summary>
public sealed class DialogPayloadValidationTests
{
    private static readonly DateTimeOffset ClientNow = new(2026, 10, 7, 18, 30, 0, TimeSpan.FromHours(2));
    private static readonly DateTimeTaskPlugin Plugin = new();
    private static readonly ICallContext Caller = FakeCallContext.AllDevices(new FakeCoreContext());

    private static DateTimeDialogViewModel Create(int count = 1) =>
        new([.. Enumerable.Range(0, count).Select(i => (IDeviceInfo)new FakeDevice(Guid.NewGuid(), $"10.0.0.{i + 1}") { Apis = Fixture.Modern })], () => ClientNow);

    /// <summary>The dialog as it opens on a real device: its time zone, mode and servers read, OK pressed.</summary>
    private static DateTimeDialogViewModel Opened(int count = 1)
    {
        var vm = Create(count);
        vm.ApplyCurrent(ViewModelTests.Current10048);
        return vm;
    }

    private static string Ok(DateTimeDialogViewModel vm)
    {
        string? json = null;
        vm.CloseRequested += (_, ok) => json = ok ? vm.ResultJson : null;
        Assert.True(vm.CanApply, vm.ApplyBlockedReason);
        vm.ApplyCommand.Execute(null);
        Assert.NotNull(json);
        return json;
    }

    private static void AssertAccepted(string json) => Assert.Empty(Plugin.ValidatePayload(json, Caller));

    [Fact]
    public void Server_time_passes_with_and_without_a_zone()
    {
        var vm = Opened();
        vm.IsServerTime = true;
        AssertAccepted(Ok(vm));

        var bare = Create(); // nothing read from the device: no zone
        bare.IsServerTime = true;
        AssertAccepted(Ok(bare));
    }

    [Fact]
    public void The_device_s_own_settings_pass_unchanged()
    {
        AssertAccepted(Ok(Opened())); // NTP as the fixture device has it, zone unchanged
    }

    [Theory]
    [InlineData("10.0.0.17")]
    [InlineData("pool.ntp.org")]
    [InlineData("time.example.com, 10.0.0.2")]
    [InlineData("a.example.com b.example.com c.example.com d.example.com e.example.com")]
    [InlineData("ntp1.example.com;ntp2.example.com")]
    [InlineData("2001:db8::1")]
    [InlineData("[2001:db8::1], ntp.example.com.")]
    [InlineData("a-b.example.com\r\n10.1.1.1")]
    public void NTP_with_static_servers_passes(string servers)
    {
        var vm = Opened();
        vm.IsNtp = true;
        vm.UseDhcp = false;
        vm.UseNts = false;
        vm.NtpServersText = servers;
        AssertAccepted(Ok(vm));
    }

    [Fact]
    public void NTP_from_DHCP_passes_with_and_without_a_server_list()
    {
        var vm = Opened();
        vm.IsNtp = true;
        vm.UseDhcp = true;
        AssertAccepted(Ok(vm));

        var empty = Opened();
        empty.UseDhcp = true;
        empty.NtpServersText = string.Empty;
        AssertAccepted(Ok(empty));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NTS_passes_with_its_servers_also_when_DHCP_is_ticked(bool dhcp)
    {
        var vm = Opened();
        vm.IsNtp = true;
        vm.UseDhcp = dhcp;
        vm.UseNts = true;
        vm.NtpServersText = "nts.example.com, 10.0.0.9";
        AssertAccepted(Ok(vm));
    }

    [Theory]
    [InlineData("2026-10-07", "18:24:01")]
    [InlineData("2026-10-07", "09:05")]
    [InlineData("1970-01-01", "00:00")]
    [InlineData("2069-12-31", "23:59:59")]
    [InlineData("2026-02-28", "9:05")]
    public void A_manual_time_passes_also_with_the_server_s_moment_it_holds(string date, string time)
    {
        var vm = Opened();
        vm.ApplyCurrent(ViewModelTests.Current10048 with { MaxYear = 2069 });
        vm.IsManual = true;
        vm.ManualDate = date;
        vm.ManualTime = time;
        var json = Ok(vm);
        AssertAccepted(json);
        Assert.NotNull(DateTimePayload.Parse(json).ManualSetAt); // the server's clock went along
    }

    [Fact]
    public void A_manual_time_passes_when_the_server_time_is_not_known()
    {
        var vm = Opened();
        vm.ApplyCurrent(ViewModelTests.Current10048 with { ServerUtc = null });
        vm.IsManual = true;
        AssertAccepted(Ok(vm));
    }

    [Fact]
    public void Daylight_saving_off_passes()
    {
        var vm = Opened();
        vm.SelectedZone = TimeZoneCatalog.Find("Europe/Vienna");
        vm.AdjustForDst = false;
        AssertAccepted(Ok(vm));
    }

    [Fact]
    public void Several_devices_pass_in_every_mode()
    {
        foreach (var mode in new[] { TimeMode.ServerTime, TimeMode.Ntp, TimeMode.Manual })
        {
            var vm = Opened(3);
            vm.Mode = mode;
            vm.SelectedZone = TimeZoneCatalog.Find("America/New_York");
            AssertAccepted(Ok(vm));
        }
    }

    /// <summary>The drop-down offers every catalog zone; none may be unknown to the validator.</summary>
    [Fact]
    public void Every_time_zone_of_the_drop_down_passes_in_every_mode()
    {
        var vm = Opened();
        vm.UseDhcp = true;
        var checkedZones = 0;
        foreach (var zone in vm.Zones)
        {
            foreach (var mode in new[] { TimeMode.Ntp, TimeMode.Manual })
            {
                vm.SelectedZone = zone;
                vm.Mode = mode;
                var json = Ok(vm);
                var errors = Plugin.ValidatePayload(json, Caller);
                Assert.True(errors.Count == 0, $"{zone.Id} ({mode}): {string.Join(" ", errors.Select(e => e.Message))}");
            }

            checkedZones++;
        }

        Assert.Equal(vm.Zones.Count, checkedZones);
    }
}
