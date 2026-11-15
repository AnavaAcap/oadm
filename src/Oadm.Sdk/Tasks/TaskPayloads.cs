using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Sdk.Tasks;

/// <summary>
/// One problem of a task payload (SDK 0.13.0, <see cref="ITaskPlugin.ValidatePayload"/>): the field and the text the dialog
/// shows below it.
/// </summary>
/// <param name="Field">The payload's JSON name of the field, nested with dots ("ntp.servers"); empty for the whole payload.</param>
/// <param name="Message">What to enter instead, in the dialog's words ("Enter at least one NTP server.").</param>
public sealed record PayloadError(string Field, string Message);

/// <summary>One device of a <see cref="TaskPlan"/>: it gets a task, or it is skipped with the reason.</summary>
/// <param name="DeviceId">The device.</param>
/// <param name="SkipReason">Null: the run changes the device; else why it is left out ("No file for this model.").</param>
public sealed record TaskPlanDevice(Guid DeviceId, string? SkipReason = null);

/// <summary>An uploaded file a run would use: its name and SHA-256 (upper-case hex) as the upload store holds them.</summary>
/// <param name="Name">The file name as it was uploaded.</param>
/// <param name="Sha256">Its SHA-256, upper-case hex.</param>
public sealed record TaskPlanFile(string Name, string Sha256);

/// <summary>
/// What a run of a payload would do on the chosen devices (SDK 0.13.0, <see cref="ITaskPlugin.PlanAsync(string?, IReadOnlyList{IDeviceInfo}, IUploadedFiles, CancellationToken)"/>): every device once,
/// to run or skipped with a reason, and the warnings of the dialog (a change that can make devices unreachable).
/// </summary>
/// <param name="Devices">Every device of the request, in its order.</param>
public sealed record TaskPlan(IReadOnlyList<TaskPlanDevice> Devices)
{
    /// <summary>The dialog's warnings for this run, in plain words; empty without.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>The uploaded files the run would use, each with its name and SHA-256 from the upload store; empty without.</summary>
    public IReadOnlyList<TaskPlanFile> Files { get; init; } = [];

    /// <summary>
    /// Problems the plan found in the payload with the uploads (an unreadable file, a choice only the user can make), each at
    /// its JSON field like <see cref="ITaskPlugin.ValidatePayload"/>: the preview then answers only these. Empty without.
    /// </summary>
    public IReadOnlyList<PayloadError> Errors { get; init; } = [];

    /// <summary>What the run does, in plain lines ("P3265-V, AXIS OS 12.11.77, aarch64 (4 devices): app_aarch64.eap"); empty without.</summary>
    public IReadOnlyList<string> Details { get; init; } = [];

    /// <summary>
    /// The plan of <see cref="ITaskPlugin.CanRun"/>: a device it cannot run on is skipped with
    /// <see cref="ITaskPlugin.NotSupportedReason"/> (or <see cref="TaskSupportReasons.Default"/>).
    /// </summary>
    public static TaskPlan ByCanRun(ITaskPlugin plugin, IReadOnlyList<IDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(devices);
        return new TaskPlan([.. devices.Select(d => plugin.CanRun(d)
            ? new TaskPlanDevice(d.Id)
            : new TaskPlanDevice(d.Id, plugin.NotSupportedReason(d) ?? TaskSupportReasons.Default))]);
    }
}

/// <summary>
/// Marks a payload property that holds a password, key or other secret (SDK 0.13.0). The host leaves it out of the task's
/// input schema, so an AI agent can never send it; a top-level one is a secret slot of the agents' preview
/// (<see cref="ITaskPlugin.SecretsOf"/>): the person types the value into their own shell or the assistant's card, and the run
/// gets it in the payload (in memory only, like every payload). Secrets inside lists need <see cref="ITaskPlugin.SecretsOf"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter | AttributeTargets.Field, AllowMultiple = false)]
public sealed class SecretAttribute : Attribute
{
    /// <summary>What the person is asked for, in lower case: "password" (the default), "passphrase", "PKCS#12 password", "license key".</summary>
    public string Label { get; set; } = "password";
}

/// <summary>
/// A secret a run of a payload needs that an AI agent never sends (SDK 0.13.0, <see cref="ITaskPlugin.SecretsOf"/>): the
/// person enters it, and the host puts it into the payload at <see cref="Field"/> for the run only.
/// </summary>
/// <param name="Name">Lower case words with underscores, unique in the payload ("password", "setting_2").</param>
/// <param name="Label">What the person is asked for, in lower case ("password", "passphrase", "PKCS#12 password").</param>
/// <param name="Field">Where the value goes in the payload: its JSON names with dots and list positions ("password", "settings[2].value"), the same names as <see cref="PayloadError.Field"/>. The host sets a text there.</param>
public sealed record TaskSecret(string Name, string Label, string Field);

/// <summary>The default secrets of a payload type (<see cref="ITaskPlugin.SecretsOf"/>): its top-level <see cref="SecretAttribute"/> properties.</summary>
public static class TaskSecrets
{
    /// <summary>
    /// One <see cref="TaskSecret"/> per top-level <see cref="SecretAttribute"/> property of <paramref name="payloadType"/> (also
    /// a record's constructor parameter), at its JSON name in <paramref name="options"/>; empty without a type.
    /// </summary>
    public static IReadOnlyList<TaskSecret> FromAttributes(Type? payloadType, System.Text.Json.JsonSerializerOptions? options)
    {
        if (payloadType is null)
        {
            return [];
        }

        var naming = (options ?? PluginJson.CreateOptions()).PropertyNamingPolicy;
        var parameters = payloadType.GetConstructors().SelectMany(c => c.GetParameters()).ToList();
        var secrets = new List<TaskSecret>();
        foreach (var property in payloadType.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            var attribute = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<SecretAttribute>(property)
                ?? parameters.Where(p => string.Equals(p.Name, property.Name, StringComparison.OrdinalIgnoreCase))
                    .Select(System.Reflection.CustomAttributeExtensions.GetCustomAttribute<SecretAttribute>).FirstOrDefault(a => a is not null);
            if (attribute is null)
            {
                continue;
            }

            var field = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>(property)?.Name
                ?? naming?.ConvertName(property.Name) ?? property.Name;
            secrets.Add(new TaskSecret(SlotName(field), attribute.Label, field));
        }

        return secrets;
    }

    /// <summary>A JSON name as a slot name: lower case letters and digits, words joined with underscores ("pkcs12Password" is "pkcs12_password").</summary>
    public static string SlotName(string field)
    {
        ArgumentNullException.ThrowIfNull(field);
        var name = new System.Text.StringBuilder(field.Length + 4);
        foreach (var c in field)
        {
            if (char.IsUpper(c) && name.Length > 0 && name[^1] != '_')
            {
                name.Append('_');
            }

            name.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_');
        }

        return name.ToString().Trim('_');
    }
}
