using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Users.Tests;

/// <summary>
/// Users for AI agents (SDK 0.13.0, docs/spec/mcp.md "Secrets"): the password is a slot of Add and of a password change,
/// never of a removal or a role change; the dialog's rules at their fields with the base password rule; the plan says what is
/// set without the value and skips a device whose stored login is the user changed or removed.
/// </summary>
public sealed class AgentTaskInputTests
{
    private static readonly ICallContext Caller = FakeCallContext.AllDevices(new FakeCoreContext());
    private readonly UsersTaskPlugin _plugin = new();
    private ITaskPlugin Plugin => _plugin;

    private static string Payload(UsersPayload payload) => UsersJson.Serialize(payload);

    [Fact]
    public void The_password_is_a_slot_only_where_one_is_set()
    {
        var slot = new TaskSecret("password", "password", "password");
        Assert.Equal([slot], Plugin.SecretsOf(Payload(new UsersPayload { Mode = UsersMode.Add, UserName = "joe" })));
        Assert.Equal([slot], Plugin.SecretsOf(Payload(new UsersPayload { Mode = UsersMode.Change, UserName = "joe", ChangePassword = true })));
        Assert.Empty(Plugin.SecretsOf(Payload(new UsersPayload { Mode = UsersMode.Change, UserName = "joe", ChangeRole = true })));
        Assert.Empty(Plugin.SecretsOf(Payload(new UsersPayload { Mode = UsersMode.Remove, UserNames = ["joe"] })));
        Assert.Same(UsersJson.Options, Plugin.PayloadJsonOptions);
    }

    [Fact]
    public void The_dialogs_rules_answer_at_their_fields()
    {
        static string Field(IReadOnlyList<PayloadError> errors) => Assert.Single(errors).Field;

        Assert.Equal("userName", Field(Plugin.ValidatePayload(Payload(new UsersPayload { Mode = UsersMode.Add, UserName = "jo e", Password = "pw" }), Caller)));
        Assert.Equal("password", Field(Plugin.ValidatePayload(Payload(new UsersPayload { Mode = UsersMode.Add, UserName = "joe" }), Caller)));
        Assert.Equal("password", Field(Plugin.ValidatePayload(Payload(new UsersPayload { Mode = UsersMode.Add, UserName = "joe", Password = "pässword" }), Caller)));
        Assert.Equal("role", Field(Plugin.ValidatePayload(Payload(new UsersPayload { Mode = UsersMode.Add, UserName = "joe", Password = "pw", Role = UserRole.None }), Caller)));
        Assert.Equal("changePassword", Field(Plugin.ValidatePayload(Payload(new UsersPayload { Mode = UsersMode.Change, UserName = "joe" }), Caller)));
        Assert.Equal("userNames", Field(Plugin.ValidatePayload(Payload(new UsersPayload { Mode = UsersMode.Remove }), Caller)));
        Assert.Equal(string.Empty, Field(Plugin.ValidatePayload("{", Caller)));
        Assert.Empty(Plugin.ValidatePayload(Payload(new UsersPayload { Mode = UsersMode.Add, UserName = "joe", Password = "S3cret!", Role = UserRole.Operator }), Caller));
        Assert.Empty(Plugin.ValidatePayload(Payload(new UsersPayload { Mode = UsersMode.Change, UserName = "joe", ChangeRole = true, Role = UserRole.Viewer }), Caller));
        Assert.Empty(Plugin.ValidatePayload(Payload(new UsersPayload { Mode = UsersMode.Remove, UserNames = ["joe", "ann"] }), Caller));
    }

    [Fact]
    public async Task The_plan_says_what_is_set_never_the_value_and_keeps_oadms_own_account()
    {
        var device = new FakeDevice { CredentialUserName = "root" };
        var other = new FakeDevice { CredentialUserName = "joe" };

        var add = await Plugin.PlanAsync(Payload(new UsersPayload { Mode = UsersMode.Add, UserName = "joe", Password = "never-shown", Role = UserRole.Operator }), [device, other], CancellationToken.None);
        Assert.All(add.Devices, d => Assert.Null(d.SkipReason));
        Assert.Equal(["Adds user joe as Operator and sets its password.", UsersTaskPlugin.PolicyCheckedOnDevice], add.Warnings);

        var change = await Plugin.PlanAsync(Payload(new UsersPayload { Mode = UsersMode.Change, UserName = "joe", ChangePassword = true }), [device, other], CancellationToken.None);
        Assert.Null(change.Devices[0].SkipReason);
        Assert.Equal(UsersTaskPlugin.OadmAccountSkip, change.Devices[1].SkipReason);
        Assert.Contains("Sets the password of user joe.", change.Warnings);

        var remove = await Plugin.PlanAsync(Payload(new UsersPayload { Mode = UsersMode.Remove, UserNames = ["ROOT"] }), [device], CancellationToken.None);
        Assert.Equal(UsersTaskPlugin.OadmAccountSkip, Assert.Single(remove.Devices).SkipReason);
        Assert.DoesNotContain(add.Warnings.Concat(change.Warnings).Concat(remove.Warnings), w => w.Contains("never-shown", StringComparison.Ordinal));
    }
}
