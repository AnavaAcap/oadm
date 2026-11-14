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

/// <summary>
/// What a run of a payload would do on the chosen devices (SDK 0.13.0, <see cref="ITaskPlugin.PlanAsync"/>): every device once,
/// to run or skipped with a reason, and the warnings of the dialog (a change that can make devices unreachable).
/// </summary>
/// <param name="Devices">Every device of the request, in its order.</param>
public sealed record TaskPlan(IReadOnlyList<TaskPlanDevice> Devices)
{
    /// <summary>The dialog's warnings for this run, in plain words; empty without.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

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
/// Marks a payload property that holds a password, key or other secret (SDK 0.13.0): a task whose payload has one is never
/// offered over MCP ("Set the password in OADM."), wherever the property sits in the payload.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter | AttributeTargets.Field, AllowMultiple = false)]
public sealed class SecretAttribute : Attribute;
