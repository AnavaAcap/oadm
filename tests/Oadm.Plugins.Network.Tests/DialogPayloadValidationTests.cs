using Oadm.Plugins.Network.Client;
using Oadm.Plugins.Network.Model;
using Oadm.Plugins.Network.Vapix;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Network.Tests;

/// <summary>
/// The server runs <c>ValidatePayload</c> of both network tasks before any task of any client starts, so every payload the
/// "Network settings" and "Assign IP address" dialogs can send must pass it. Each dialog's view model is driven through
/// Apply (or Finish) and the exact JSON it completes with is handed to the validator.
/// </summary>
public sealed class DialogPayloadValidationTests
{
    private static readonly NetworkSettingsTaskPlugin Settings = new();
    private static readonly AssignIpTaskPlugin Assign = new();
    private static readonly ICallContext Caller = FakeCallContext.AllDevices(new FakeCoreContext());

    private static readonly CurrentNetworkSettings Current =
        NetworkInfoParser.WithIpv6Parameters(
            NetworkInfoParser.ParseGetNetworkInfo(Fixture.Read(Fixture.GetNetworkInfo), "10.0.0.48"),
            Fixture.Parameters(Fixture.ParamNetwork));

    private static List<IDeviceInfo> Devices(int count) =>
        [.. Enumerable.Range(0, count).Select(i => (IDeviceInfo)new FakeDevice(Guid.NewGuid(), $"10.0.0.{48 + i}", Serial: $"ACCC8E00000{i + 1}"))];

    private static void AssertAccepted(ITaskPlugin plugin, string? json)
    {
        Assert.False(string.IsNullOrEmpty(json));
        var errors = plugin.ValidatePayload(json, Caller);
        Assert.True(errors.Count == 0, string.Join(" | ", errors.Select(e => e.Field + ": " + e.Message)) + " in " + json);
    }

    private static NetworkSettingsViewModel Dialog(int devices, Action<NetworkSettingsViewModel> fill)
    {
        var vm = new NetworkSettingsViewModel(Devices(devices));
        vm.ApplyCurrent(Current);
        fill(vm);
        return vm;
    }

    private static async Task<string?> ApplyAsync(NetworkSettingsViewModel vm)
    {
        Assert.True(vm.CanApply, vm.ApplyBlockedReason);
        await vm.ApplyCommand.ExecuteAsync(null);
        return vm.ResultJson;
    }

    private static void Pick(NetworkSettingsViewModel vm, Ipv4Choice? v4 = null, Ipv6Choice? v6 = null, SourceChoice? dns = null, SourceChoice? host = null)
    {
        if (v4 is { } a)
        {
            vm.SelectedIpv4 = vm.Ipv4Choices.Single(c => c.Value == a);
        }

        if (v6 is { } b)
        {
            vm.SelectedIpv6 = vm.Ipv6Choices.Single(c => c.Value == b);
        }

        if (dns is { } c1)
        {
            vm.SelectedDns = vm.DnsChoices.Single(c => c.Value == c1);
        }

        if (host is { } d)
        {
            vm.SelectedHostName = vm.HostNameChoices.Single(c => c.Value == d);
        }
    }

    // ---- Network settings ----

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task IPv4_from_DHCP_passes(int devices)
    {
        var vm = Dialog(devices, v => Pick(v, v4: Ipv4Choice.Dhcp));
        AssertAccepted(Settings, await ApplyAsync(vm));
    }

    [Fact]
    public async Task A_static_IPv4_address_of_one_device_passes_with_a_mask_as_prefix_or_dotted()
    {
        foreach (var mask in new[] { "255.255.255.0", "24", "255.255.0.0", "/16" })
        {
            var vm = Dialog(1, v =>
            {
                Pick(v, v4: Ipv4Choice.Static);
                v.Ipv4Address = "10.0.0.60";
                v.Ipv4Mask = mask;
                v.Ipv4Gateway = mask is "255.255.0.0" or "/16" ? "10.0.1.1" : "10.0.0.138";
            });
            AssertAccepted(Settings, await ApplyAsync(vm));
        }
    }

    [Fact]
    public async Task Static_IPv4_addresses_of_several_devices_in_the_table_pass()
    {
        var vm = Dialog(4, v => Pick(v, v4: Ipv4Choice.Static));
        vm.Assignment.Rows[1].NewAddress = "10.0.0.101";
        vm.Assignment.Rows[3].NewAddress = "10.0.0.103";
        AssertAccepted(Settings, await ApplyAsync(vm));
    }

    [Theory]
    [InlineData(1, Ipv6Choice.Disabled)]
    [InlineData(1, Ipv6Choice.Auto)]
    [InlineData(1, Ipv6Choice.Dhcp)]
    [InlineData(3, Ipv6Choice.Disabled)]
    [InlineData(3, Ipv6Choice.Auto)]
    [InlineData(3, Ipv6Choice.Dhcp)]
    public async Task Every_IPv6_mode_without_an_address_passes(int devices, Ipv6Choice mode)
    {
        var vm = Dialog(devices, v => Pick(v, v6: mode));
        AssertAccepted(Settings, await ApplyAsync(vm));
    }

    [Theory]
    [InlineData("")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    public async Task A_static_IPv6_address_of_one_device_passes_with_and_without_a_gateway(string gateway)
    {
        var vm = Dialog(1, v =>
        {
            Pick(v, v6: Ipv6Choice.Static);
            v.Ipv6Address = "2001:db8::60";
            v.Ipv6Prefix = "64";
            v.Ipv6Gateway = gateway;
        });
        AssertAccepted(Settings, await ApplyAsync(vm));
    }

    [Fact]
    public async Task Static_IPv6_addresses_of_several_devices_pass()
    {
        var vm = Dialog(3, v => Pick(v, v6: Ipv6Choice.Static));
        vm.Ipv6Prefix = "48";
        for (var i = 0; i < 3; i++)
        {
            vm.Assignment.Rows[i].NewIpv6Address = $"2001:db8::{i + 10}";
        }

        AssertAccepted(Settings, await ApplyAsync(vm));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task DNS_from_DHCP_passes(int devices)
    {
        var vm = Dialog(devices, v => Pick(v, dns: SourceChoice.Dhcp));
        AssertAccepted(Settings, await ApplyAsync(vm));
    }

    [Theory]
    [InlineData("1.1.1.1", "", "", "")]
    [InlineData("1.1.1.1", "8.8.8.8", "", "")]
    [InlineData("1.1.1.1", "", "example.com", "")]
    [InlineData("1.1.1.1", "8.8.8.8", "lan.example.com", "a.example.com, b.example.com")]
    [InlineData("10.0.0.2", "", "", "corp.local; office.local")]
    public async Task Static_DNS_passes_with_each_optional_field(string primary, string secondary, string domain, string search)
    {
        var vm = Dialog(1, v =>
        {
            Pick(v, dns: SourceChoice.Static);
            v.DnsPrimary = primary;
            v.DnsSecondary = secondary;
            v.DnsDomain = domain;
            v.DnsSearch = search;
        });
        AssertAccepted(Settings, await ApplyAsync(vm));
    }

    [Fact]
    public async Task Host_names_pass_for_DHCP_a_single_device_and_templates()
    {
        var dhcp = Dialog(2, v => Pick(v, host: SourceChoice.Dhcp));
        AssertAccepted(Settings, await ApplyAsync(dhcp));

        var single = Dialog(1, v =>
        {
            Pick(v, host: SourceChoice.Static);
            v.HostNameText = "cam-1";
        });
        AssertAccepted(Settings, await ApplyAsync(single));

        foreach (var template in new[] { "cam-{n}", "axis-{serial}", "lobby{n}-{serial}" })
        {
            var many = Dialog(3, v =>
            {
                Pick(v, host: SourceChoice.Static);
                v.HostNameText = template;
            });
            AssertAccepted(Settings, await ApplyAsync(many));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task All_sections_at_once_pass(int devices)
    {
        var vm = Dialog(devices, v =>
        {
            Pick(v, v4: Ipv4Choice.Static, v6: Ipv6Choice.Static, dns: SourceChoice.Static, host: SourceChoice.Static);
            v.Ipv4Mask = "24";
            v.Ipv4Gateway = "10.0.0.138";
            v.Ipv6Prefix = "64";
            v.DnsPrimary = "1.1.1.1";
            v.DnsSecondary = "8.8.8.8";
            v.DnsDomain = "example.com";
            v.DnsSearch = "example.com";
            if (devices == 1)
            {
                v.Ipv4Address = "10.0.0.60";
                v.Ipv6Address = "2001:db8::60";
                v.HostNameText = "cam-1";
            }
            else
            {
                v.HostNameText = "cam-{n}";
            }
        });
        if (devices > 1)
        {
            for (var i = 0; i < devices; i++)
            {
                vm.Assignment.Rows[i].NewIpv6Address = $"2001:db8::{i + 10}";
            }
        }

        AssertAccepted(Settings, await ApplyAsync(vm));
    }

    // ---- Assign IP address ----

    private static AssignIpViewModel AssignDialog(int devices, string range, Action<AssignIpViewModel>? fill = null)
    {
        var vm = new AssignIpViewModel(Devices(devices));
        vm.ApplyCurrent(Current);
        vm.IpRange = range;
        fill?.Invoke(vm);
        return vm;
    }

    private static async Task<string?> FinishAsync(AssignIpViewModel vm)
    {
        if (!vm.UseDhcp)
        {
            await vm.PrimaryCommand.ExecuteAsync(null); // Next: the review page with the suggested addresses
            Assert.True(vm.IsReviewPage);
        }

        Assert.True(vm.CanContinue, vm.BlockedReason);
        await vm.PrimaryCommand.ExecuteAsync(null);
        return vm.ResultJson;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Assign_IP_from_DHCP_passes(int devices)
    {
        var vm = new AssignIpViewModel(Devices(devices)) { UseDhcp = true };
        AssertAccepted(Assign, await FinishAsync(vm));
    }

    [Theory]
    [InlineData(1, "10.0.0.90", "", "")]
    [InlineData(3, "10.0.0.100-110", "", "")]
    [InlineData(3, "10.0.0.100-110", "10.0.0.2", "")]
    [InlineData(3, "10.0.0.100-110", "10.0.0.2", "8.8.8.8")]
    [InlineData(3, "10.0.0.100-110", "", "8.8.8.8")]
    public async Task Assign_IP_from_a_range_passes_with_zero_one_or_two_DNS_servers(int devices, string range, string primary, string secondary)
    {
        var vm = AssignDialog(devices, range, v =>
        {
            v.DnsPrimary = primary;
            v.DnsSecondary = secondary;
        });
        AssertAccepted(Assign, await FinishAsync(vm));
    }

    [Fact]
    public async Task Assign_IP_passes_after_an_address_was_edited_in_the_table()
    {
        var vm = AssignDialog(3, "10.0.0.100-110");
        await vm.PrimaryCommand.ExecuteAsync(null);
        vm.Assignment.Rows[1].NewAddress = "10.0.0.150";
        AssertAccepted(Assign, await FinishAsync(vm));
    }
}
