using System.Text.Json;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;

namespace Oadm.Plugins.Users;

/// <summary>
/// The input of AI agents (SDK 0.13.0, docs/spec/mcp.md "Secrets"): the dialog's payload without its password, which the
/// person enters through the secret channel (Add, and Change with a new password); Remove needs none. The checks are the
/// dialog's (<see cref="UserChangePlanner.ValidatePayload"/>) at their fields with the base password rule; each device reads
/// its passphrase policy and the account OADM uses and checks both again before the write.
/// </summary>
public sealed partial class UsersTaskPlugin
{
    /// <summary>The warning of a plan that sets a password: each device checks its own rules first.</summary>
    public const string PolicyCheckedOnDevice = "Each device checks the password against its own password rules before the change.";

    /// <summary>The skip of a device whose stored login is the user a run would change or remove.</summary>
    public const string OadmAccountSkip = "OADM uses this account on the device.";

    /// <inheritdoc />
    public System.Text.Json.JsonSerializerOptions? PayloadJsonOptions => UsersJson.Options;

    /// <summary>The password, for Add and for Change with a new password; none for Remove or a role change only.</summary>
    public IReadOnlyList<TaskSecret> SecretsOf(string? payloadJson)
    {
        var payload = TryParse(payloadJson);
        return payload is { Mode: UsersMode.Add } or { Mode: UsersMode.Change, ChangePassword: true }
            ? [new TaskSecret("password", "password", "password")]
            : [];
    }

    /// <summary>The dialog's rules, each at its field (<c>mode</c>, <c>userName</c>, <c>userNames</c>, <c>role</c>, <c>password</c>).</summary>
    public IReadOnlyList<PayloadError> ValidatePayload(string? payloadJson, ICallContext caller)
    {
        var payload = TryParse(payloadJson);
        if (payload is null)
        {
            return [new PayloadError(string.Empty, "The task settings could not be read. Nothing was changed.")];
        }

        var errors = new List<PayloadError>();
        void Add(string field, string? reason)
        {
            if (reason is not null)
            {
                errors.Add(new PayloadError(field, reason));
            }
        }

        if (payload.Mode == UsersMode.Remove)
        {
            var names = payload.RemoveNames;
            Add("userNames", names.Count == 0 ? "Choose the users to remove." : names.Count > UsersPayload.MaxRemoveUsers ? $"Remove at most {UsersPayload.MaxRemoveUsers} users at once." : null);
            for (var i = 0; i < names.Count && errors.Count < 20; i++)
            {
                Add(payload.UserNames is { Count: > 0 } ? $"userNames[{i}]" : "userName", CredentialRules.ValidateUserName(names[i]));
            }

            return errors;
        }

        Add("userName", CredentialRules.ValidateUserName(payload.UserName));
        switch (payload.Mode)
        {
            case UsersMode.Add:
                Add("role", payload.Role == UserRole.None ? "Choose a role." : null);
                Add("password", CredentialRules.ValidatePassword(payload.Password, PassphrasePolicy.None));
                break;
            case UsersMode.Change:
                Add("changePassword", !payload.ChangePassword && !payload.ChangeRole ? "Choose what to change: password, role or both." : null);
                Add("role", payload.ChangeRole && payload.Role == UserRole.None ? "Choose a role." : null);
                Add("password", payload.ChangePassword ? CredentialRules.ValidatePassword(payload.Password, PassphrasePolicy.None) : null);
                break;
            default:
                Add("mode", "Choose add, change or remove.");
                break;
        }

        return errors;
    }

    /// <summary>
    /// From the cached data only: the devices that can run it, a device whose stored login is the user a change of password or
    /// a removal names is skipped (the task refuses it anyway), and the warnings say what is set (never the value).
    /// </summary>
    public Task<TaskPlan> PlanAsync(string? payloadJson, IReadOnlyList<IDeviceInfo> devices, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var payload = UsersJson.ParsePayload(payloadJson);
        var touched = payload.Mode == UsersMode.Remove ? payload.RemoveNames
            : payload.Mode == UsersMode.Change && (payload.ChangePassword || payload.ChangeRole) ? [payload.UserName.Trim()] : [];
        var plan = devices.Select(d => !CanRun(d)
                ? new TaskPlanDevice(d.Id, NotSupportedReason(d) ?? TaskSupportReasons.Default)
                : d.CredentialUserName is { } login && touched.Any(n => string.Equals(n, login, StringComparison.OrdinalIgnoreCase))
                    ? new TaskPlanDevice(d.Id, OadmAccountSkip)
                    : new TaskPlanDevice(d.Id))
            .ToList();
        var name = payload.UserName.Trim();
        List<string> warnings = payload.Mode switch
        {
            UsersMode.Add => [$"Adds user {name} as {UserRoles.Describe(payload.Role, payload.Ptz)} and sets its password.", PolicyCheckedOnDevice],
            UsersMode.Change when payload.ChangePassword => [$"Sets the password of user {name}.", .. RoleLine(payload), PolicyCheckedOnDevice],
            UsersMode.Change => [.. RoleLine(payload)],
            _ => [$"Removes {(payload.RemoveNames.Count == 1 ? "user " : "users ")}{string.Join(", ", payload.RemoveNames)}. A device keeps the account OADM uses and its last administrator."],
        };
        return Task.FromResult(new TaskPlan(plan) { Warnings = warnings });
    }

    private static IEnumerable<string> RoleLine(UsersPayload payload) =>
        payload.ChangeRole ? [$"Makes user {payload.UserName.Trim()} {UserRoles.Describe(payload.Role, payload.Ptz)}."] : [];

    private static UsersPayload? TryParse(string? payloadJson)
    {
        try
        {
            return UsersJson.ParsePayload(payloadJson);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
