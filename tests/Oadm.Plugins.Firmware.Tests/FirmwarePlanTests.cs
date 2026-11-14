using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Firmware.Tests;

/// <summary>
/// The plan of the tools (<see cref="FirmwareTaskPlugin.PlanAsync"/>) is the dialog's choice (<see cref="FirmwareCompatibility.Choose"/>)
/// from the cached model and version, never a device request; the payload of the tools is read by the same task.
/// </summary>
public sealed class FirmwarePlanTests
{
    private static string Payload(string fileName, FactoryDefaultMode mode, bool allowDowngrade, params Guid[] devices) =>
        new FirmwarePayload
        {
            Files = [new FirmwareFile("0123456789abcdef0123456789abcdef", fileName, devices.Length == 0 ? null : devices)],
            FactoryDefaultMode = mode,
            AllowDowngrade = allowDowngrade,
        }.ToJson();

    [Theory]
    [InlineData("P3265-V", "12.8.0", FactoryDefaultMode.None, false)] // an upgrade
    [InlineData("P3265-V", "12.11.77", FactoryDefaultMode.None, false)] // already up to date
    [InlineData("P3265-V", "12.12.0", FactoryDefaultMode.None, false)] // a downgrade that is not allowed
    [InlineData("P3265-V", "12.12.0", FactoryDefaultMode.None, true)] // a downgrade that keeps the settings
    [InlineData("P3265-V", "12.12.0", FactoryDefaultMode.Soft, true)] // a downgrade with a factory default
    [InlineData("M3085-V", "12.8.0", FactoryDefaultMode.None, false)] // another model
    [InlineData("P3265-V", null, FactoryDefaultMode.Hard, false)] // an unknown version with a factory default
    public async Task ADeviceRunsExactlyWhenTheDialogsChoiceInstallsAndOtherwiseSaysWhy(string model, string? version, FactoryDefaultMode mode, bool allowDowngrade)
    {
        var plugin = new FirmwareTaskPlugin();
        var device = new FakeDevice(DeviceStatus.Ok, model, version!, Fixture.Fwmgr110);
        var payload = Payload("P3265-V_12_11_77.bin", mode, allowDowngrade);

        var plan = await plugin.PlanAsync(payload, [device], CancellationToken.None);

        var image = FirmwareImageInspector.Inspect("P3265-V_12_11_77.bin", FirmwareImageInspector.MinimumSize);
        var (_, check) = FirmwareCompatibility.Choose(model, version, [image], mode, allowDowngrade);
        var entry = Assert.Single(plan.Devices);
        Assert.Equal(device.Id, entry.DeviceId);
        Assert.Equal(check.WillInstall, entry.SkipReason is null);
        if (!check.WillInstall)
        {
            Assert.Equal(check.Message, entry.SkipReason);
        }

        Assert.Equal(FirmwareWarnings.Of(mode) is { } warning ? [warning] : [], plan.Warnings);
    }

    [Fact]
    public async Task AFileOfADeviceListIsOnlyForThoseDevicesAndTheOthersHaveNoFileForTheirModel()
    {
        var plugin = new FirmwareTaskPlugin();
        var named = new FakeDevice(DeviceStatus.Ok, "P3265-V", "12.8.0", Fixture.Fwmgr110);
        var left = new FakeDevice(DeviceStatus.Ok, "P3265-V", "12.8.0", Fixture.Fwmgr110);
        var unreachable = new FakeDevice(DeviceStatus.Unreachable, "P3265-V", "12.8.0", Fixture.Fwmgr110);
        var payload = Payload("P3265-V_12_11_77.bin", FactoryDefaultMode.None, false, named.Id, unreachable.Id);

        var plan = await plugin.PlanAsync(payload, [named, left, unreachable], CancellationToken.None);

        Assert.Null(plan.Devices[0].SkipReason);
        Assert.Equal(FirmwareCompatibility.NoFileForModel, plan.Devices[1].SkipReason);
        Assert.Equal(plugin.NotSupportedReason(unreachable), plan.Devices[2].SkipReason); // the cached status says no first
    }

    [Fact]
    public void TheToolsPayloadIsReadByTheTaskAndItsRulesAreTheDialogs()
    {
        var plugin = new FirmwareTaskPlugin();
        var caller = Oadm.Sdk.Testing.FakeCallContext.AllDevices(new Oadm.Sdk.Testing.FakeCoreContext());
        var text = """{"files":[{"fileId":"abc","fileName":"P3265-V_12_11_77.bin"}],"factoryDefaultMode":"soft","allowDowngrade":true}""";
        var payload = FirmwarePayload.Parse(text);
        Assert.Equal(FactoryDefaultMode.Soft, payload.FactoryDefaultMode);
        Assert.True(payload.AllowDowngrade);
        Assert.Equal("abc", Assert.Single(payload.AllFiles()).FileId);
        Assert.Empty(plugin.ValidatePayload(text, caller));

        var notBin = plugin.ValidatePayload("""{"files":[{"fileId":"abc","fileName":"P3265-V_12_11_77.zip"}]}""", caller);
        var error = Assert.Single(notBin);
        Assert.Equal("files[0].fileName", error.Field);
        Assert.Equal("Not an AXIS OS image: the file must be a .bin file.", error.Message);
        Assert.Equal("No AXIS OS file was selected. Nothing was changed.", Assert.Single(plugin.ValidatePayload("{}", caller)).Message);
        Assert.Equal(typeof(FirmwareMcpPayload), plugin.PayloadType);
    }
}
