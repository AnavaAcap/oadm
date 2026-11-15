using Oadm.Plugins.Users.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Users.Tests;

/// <summary>
/// The server runs <c>ValidatePayload</c> of a task before any task of any client starts. The Users task has no check of its
/// own before the run (the default accepts every payload; the dialog's rules, <see cref="UserChangePlanner.ValidatePayload"/>,
/// run per device against that device's passphrase policy). The dialog's view model is driven through each mode and the exact
/// JSON it completes with is handed to the plugin's validator and to those per-device rules.
/// </summary>
public sealed class DialogPayloadValidationTests
{
    private static readonly ITaskPlugin Plugin = new UsersTaskPlugin();
    private static readonly ICallContext Caller = FakeCallContext.AllDevices(new FakeCoreContext());

    private static UsersDialogViewModel Create(int devices = 1) =>
        new(Enumerable.Range(0, devices).Select(i => (IDeviceInfo)new FakeDevice { Address = $"10.0.0.{48 + i}" }).ToList());

    private static void AssertAccepted(UsersDialogViewModel vm)
    {
        Assert.True(vm.CanApply, vm.ApplyBlockedReason);
        var json = vm.BuildPayload();
        Assert.False(string.IsNullOrEmpty(json));
        var errors = Plugin.ValidatePayload(json, Caller);
        Assert.True(errors.Count == 0, string.Join(" | ", errors.Select(e => e.Field + ": " + e.Message)) + " in " + json);
        UserChangePlanner.ValidatePayload(UsersJson.ParsePayload(json), vm.Policy); // what each device's task runs before any write
    }

    [Theory]
    [InlineData(1, 0, false)]
    [InlineData(1, 1, false)]
    [InlineData(1, 2, true)]
    [InlineData(3, 0, true)]
    [InlineData(3, 1, false)]
    [InlineData(3, 2, false)]
    public void Add_passes_with_each_role_with_and_without_PTZ(int devices, int role, bool ptz)
    {
        var vm = Create(devices);
        vm.UserName = "joe";
        vm.Password = vm.ConfirmPassword = "pw";
        vm.SelectedRole = vm.Roles[role];
        vm.Ptz = ptz;
        AssertAccepted(vm);
    }

    [Theory]
    [InlineData(PassphrasePolicy.None, "pw")]
    [InlineData(PassphrasePolicy.Complex, "Abcdefghij1!")]
    public void Add_passes_under_each_passphrase_policy_the_device_reports(PassphrasePolicy policy, string password)
    {
        var vm = Create(2);
        vm.Apply(UsersDialogViewModelTests.RecordedResult() with { Policy = policy });
        vm.UserName = "JoeSmith1";
        vm.Password = vm.ConfirmPassword = password;
        AssertAccepted(vm);
    }

    [Theory]
    [InlineData(true, false, 0, false)]
    [InlineData(false, true, 0, false)]
    [InlineData(false, true, 2, true)]
    [InlineData(true, true, 1, false)]
    [InlineData(true, true, 2, true)]
    public void Change_passes_for_the_password_the_role_or_both(bool password, bool role, int roleIndex, bool ptz)
    {
        var vm = Create(2);
        vm.IsChange = true;
        vm.UserName = "acs";
        vm.ChangePassword = password;
        vm.ChangeRole = role;
        if (password)
        {
            vm.Password = vm.ConfirmPassword = "newpw";
        }

        if (role)
        {
            vm.SelectedRole = vm.Roles[roleIndex];
            vm.Ptz = ptz;
        }

        AssertAccepted(vm);
    }

    [Fact]
    public void Remove_passes_for_one_user_and_for_several()
    {
        var one = Create();
        one.Apply(UsersDialogViewModelTests.RecordedResult());
        one.IsRemove = true;
        one.SetSelectedUsers([one.ExistingUsers[3]]);
        AssertAccepted(one);

        var many = Create(3);
        many.Apply(UsersDialogViewModelTests.RecordedResult());
        many.IsRemove = true;
        many.SetSelectedUsers([many.ExistingUsers[1], many.ExistingUsers[3]]);
        AssertAccepted(many);
    }
}
