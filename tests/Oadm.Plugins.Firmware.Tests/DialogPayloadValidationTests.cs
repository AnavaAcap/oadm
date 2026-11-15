extern alias fwclient;

using Oadm.Plugins.Firmware.Tests;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

using DialogViewModel = fwclient::Oadm.Plugins.Firmware.Client.FirmwareDialogViewModel;

// Own namespace: inside Oadm.Plugins.Firmware.* the server's copies of the shared types would win.
namespace Oadm.FirmwareDialogTests;

/// <summary>
/// The server runs <see cref="Oadm.Plugins.Firmware.FirmwareTaskPlugin.ValidatePayload"/> before any task of any client starts, so
/// every payload the "Upgrade AXIS OS" dialog can send must pass it: the dialog's view model is driven through Upgrade and the
/// exact JSON it completes with is handed to the validator.
/// </summary>
public sealed class DialogPayloadValidationTests
{
    private static readonly Oadm.Plugins.Firmware.FirmwareTaskPlugin Plugin = new();
    private static readonly ICallContext Caller = FakeCallContext.AllDevices(new FakeCoreContext());

    private static readonly Device[] Devices =
    [
        new("10.0.0.48", "P3265-V", "11.11.160"),
        new("10.0.0.49", "P3265-V", "12.11.77"),
        new("10.0.0.50", "Q6135-LE", "11.11.160"),
        new("10.0.0.51", "P3265-V", "12.20.10"),
    ];

    private static void AssertAccepted(string? json)
    {
        Assert.False(string.IsNullOrEmpty(json));
        var errors = Plugin.ValidatePayload(json, Caller);
        Assert.True(errors.Count == 0, string.Join(" | ", errors.Select(e => e.Field + ": " + e.Message)) + " in " + json);
    }

    [Theory]
    [InlineData(0, false, "C:\\fw\\P3265-V_12_11_77.bin")]
    [InlineData(1, false, "C:\\fw\\P3265-V_12_11_77.bin")]
    [InlineData(2, false, "/home/me/fw/P3265-V_12_11_77.bin")]
    [InlineData(0, true, "P3265-V_12_11_77.bin")]
    [InlineData(1, true, "C:\\fw\\P3265-V_12_11_77.bin")]
    [InlineData(2, true, "C:\\fw\\AXIS_P3265-V_12.11.77.bin")]
    [InlineData(0, false, "C:\\fw\\P3265-V_12_11_77_beta1.BIN")]
    public async Task A_local_file_passes_with_each_factory_default_mode_with_and_without_a_downgrade(int mode, bool allowDowngrade, string path)
    {
        using var vm = new DialogViewModel(new FakeDialogContext(), Devices, new FakeFileSource());
        await vm.SelectFileAsync(path, CancellationToken.None);
        vm.AllowDowngrade = allowDowngrade;
        vm.SelectedMode = vm.Modes[mode];

        await vm.StartCommand.ExecuteAsync(null);

        AssertAccepted(vm.Result);
    }

    [Fact]
    public async Task One_file_per_model_passes_with_and_without_device_lists()
    {
        using var vm = new DialogViewModel(new FakeDialogContext(), Devices, new FakeFileSource());
        await vm.SelectFilesAsync(["C:\\fw\\P3265-V_12_11_77.bin", "C:\\fw\\Q6135-LE_12_11_77.bin"], CancellationToken.None);

        await vm.StartCommand.ExecuteAsync(null);

        AssertAccepted(vm.Result);
        Assert.Equal(2, Oadm.Plugins.Firmware.FirmwarePayload.Parse(vm.Result).AllFiles().Count);

        // A file for named devices only (what the dialog writes when the devices of a model choose differently): built with
        // the dialog's own payload type and serializer, as the view model does.
        var named = new fwclient::Oadm.Plugins.Firmware.FirmwarePayload
        {
            FileId = "file-1",
            FileName = "P3265-V_12_11_77.bin",
            Files = [new("file-1", "P3265-V_12_11_77.bin", [Devices[0].Id, Devices[3].Id]), new("file-2", "Q6135-LE_12_11_77.bin", [Devices[2].Id])],
        }.ToJson();
        AssertAccepted(named);
    }

    [Fact]
    public async Task A_file_without_a_product_in_its_name_passes_when_the_dialog_lets_it_start()
    {
        using var vm = new DialogViewModel(new FakeDialogContext(), Devices, new FakeFileSource());
        await vm.SelectFileAsync("C:\\fw\\firmware.bin", CancellationToken.None);
        if (vm.StartCommand.CanExecute(null))
        {
            await vm.StartCommand.ExecuteAsync(null);
            AssertAccepted(vm.Result);
        }
    }

    private static Device[] OnlineSelection() =>
    [
        new("10.0.0.48", "P3265-V", "11.11.160"),
        new("10.0.0.49", "P3265-V", "11.11.160"),
        new("10.0.0.50", "P3265-V", "12.11.77"),
        new("10.0.0.51", "P3265-V", "12.11.147"),
        new("10.0.0.52", "C1410", "11.11.238"),
        new("10.0.0.53", "P12-MkIII", "9.80.1"),
    ];

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public async Task A_version_from_axis_com_passes_with_each_mode_and_a_downgrade(int mode, bool allowDowngrade)
    {
        var ctx = FirmwareOnlineDialogTests.Context(new FakeAxisSite { MakeVersionFolders = true });
        using var vm = new DialogViewModel(ctx, OnlineSelection(), new FakeFileSource());
        await vm.LoadOnlineAsync(CancellationToken.None);
        vm.AllowDowngrade = allowDowngrade;
        vm.SelectedMode = vm.Modes[mode];

        await vm.StartCommand.ExecuteAsync(null);

        AssertAccepted(vm.Result);
        Assert.NotEmpty(Oadm.Plugins.Firmware.FirmwarePayload.Parse(vm.Result).AllOnline());
    }

    [Fact]
    public async Task Every_rule_button_and_a_version_chosen_for_one_device_pass()
    {
        var ctx = FirmwareOnlineDialogTests.Context(new FakeAxisSite { MakeVersionFolders = true });
        using var vm = new DialogViewModel(ctx, OnlineSelection(), new FakeFileSource());
        await vm.LoadOnlineAsync(CancellationToken.None);

        foreach (var rule in new[] { vm.LatestLtsCommand, vm.LatestActiveCommand, vm.RecommendedCommand })
        {
            if (rule.CanExecute(null))
            {
                rule.Execute(null);
            }

            vm.AllowDowngrade = true;
            await vm.StartCommand.ExecuteAsync(null);
            AssertAccepted(vm.Result);
        }

        var one = vm.Devices[1];
        one.SelectedChoice = one.Choices.Single(c => c.Text == "12.11.147");
        await vm.StartCommand.ExecuteAsync(null);
        AssertAccepted(vm.Result);
        Assert.Contains(Oadm.Plugins.Firmware.FirmwarePayload.Parse(vm.Result).AllOnline(), o => o.Devices is not null);
    }

    [Fact]
    public async Task A_run_that_mixes_a_local_file_and_versions_from_axis_com_passes()
    {
        var ctx = FirmwareOnlineDialogTests.Context(new FakeAxisSite { MakeVersionFolders = true });
        Device[] devices = [.. OnlineSelection(), new("10.0.0.60", "Q6135-LE", "11.11.160")];
        using var vm = new DialogViewModel(ctx, devices, new FakeFileSource { Picked = "C:\\fw\\Q6135-LE_12_11_77.bin" });
        await vm.LoadOnlineAsync(CancellationToken.None);
        await vm.SelectFileAsync("C:\\fw\\Q6135-LE_12_11_77.bin", CancellationToken.None);

        await vm.StartCommand.ExecuteAsync(null);

        AssertAccepted(vm.Result);
        var payload = Oadm.Plugins.Firmware.FirmwarePayload.Parse(vm.Result);
        Assert.NotEmpty(payload.AllOnline());
        Assert.NotEmpty(payload.AllFiles());
    }
}
