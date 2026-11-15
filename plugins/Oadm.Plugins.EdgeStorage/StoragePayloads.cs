using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json.Serialization;

using Oadm.Plugins.EdgeStorage.Device;
using Oadm.Sdk.Tasks;

namespace Oadm.Plugins.EdgeStorage;

/// <summary>
/// The SD card actions an AI agent can start (docs/spec/mcp.md "Tasks"). Encrypt needs a passphrase and is done on the
/// page. The names are the <see cref="StorageActions"/> texts the task reads.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<StorageMcpAction>))]
public enum StorageMcpAction
{
    [JsonStringEnumMemberName(StorageActions.Check)]
    Check,

    [JsonStringEnumMemberName(StorageActions.Repair)]
    Repair,

    [JsonStringEnumMemberName(StorageActions.Retention)]
    Retention,

    [JsonStringEnumMemberName(StorageActions.Format)]
    Format,

    [JsonStringEnumMemberName(StorageActions.EncryptionOff)]
    EncryptionOff,
}

/// <summary>
/// The input of the SD card task for AI agents (SDK 0.13.0): the page's <see cref="TaskPayload"/> without the passphrase. The
/// task reads the same JSON as a <see cref="TaskPayload"/>.
/// </summary>
[Description("Check, repair or format the SD cards, turn off their encryption, or set how long recordings are kept.")]
public sealed record StorageMcpPayload(
    [property: Description("Format and encryptionOff delete every recording on the card. Check, repair and format stop recording while they run.")] StorageMcpAction Action,
    [property: Description("Retention only: days recordings are kept, 0 = until the card is full."), Range(0, StorageRequests.MaxRetentionDays)] int? RetentionDays = null);

/// <summary>The payload rules of the SD card task, shared by the task, the page method and the agents' check.</summary>
public static class StoragePayloadRules
{
    /// <summary>The field of the action.</summary>
    public const string ActionField = "action";

    /// <summary>The field of the retention.</summary>
    public const string RetentionField = "retentionDays";

    /// <summary>The field of the passphrase.</summary>
    public const string PassphraseField = "passphrase";

    /// <summary>An action the task does not know.</summary>
    public const string UnknownAction = "Unknown action.";

    /// <summary>Encrypt outside the page: it needs a passphrase, which agents never send.</summary>
    public const string EncryptOnPage = "Encrypt on the Edge storage page. It needs a passphrase.";

    /// <summary>The warning of format and encryption off.</summary>
    public const string DeletesRecordings = "The SD cards are formatted: every recording on them is deleted.";

    /// <summary>The warning of the actions that unmount the card.</summary>
    public const string StopsRecording = "Recording stops until the task is finished.";

    /// <summary>The warning of repair, as the page's confirmation says.</summary>
    public const string RepairMayLose = "Damaged recordings can be lost.";

    /// <summary>"Enter 0 to 7,000 days."</summary>
    public static string RetentionRange { get; } = string.Create(CultureInfo.InvariantCulture, $"Enter 0 to {StorageRequests.MaxRetentionDays:N0} days.");

    /// <summary>
    /// The problems of a payload, one per field, before any device is touched (the page's and the task's rules):
    /// a known action, the retention days, the passphrase of an encrypt.
    /// </summary>
    public static IReadOnlyList<PayloadError> Problems(TaskPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!StorageActions.IsKnown(payload.Action))
        {
            return [new(ActionField, UnknownAction)];
        }

        if (payload.Action == StorageActions.Retention && payload.RetentionDays is not (>= 0 and <= StorageRequests.MaxRetentionDays))
        {
            return [new(RetentionField, RetentionRange)];
        }

        if (payload.Action == StorageActions.EncryptionOn && StorageTaskPlugin.PassphraseProblem(payload.Passphrase) is { } problem)
        {
            return [new(PassphraseField, problem)];
        }

        return [];
    }

    /// <summary>
    /// The core plugin's permission one action needs: check and retention "manage", the others "format" (they delete
    /// recordings). Unknown: both (fail closed).
    /// </summary>
    public static IReadOnlyList<string> PermissionsOf(string? action) =>
        StorageActions.IsChange(action) ? [EdgeStorageMethods.ManagePermission]
        : StorageActions.IsKnown(action) ? [EdgeStorageMethods.FormatPermission]
        : [EdgeStorageMethods.ManagePermission, EdgeStorageMethods.FormatPermission];

    /// <summary>The warnings of a run: what is lost and that recording stops.</summary>
    public static IReadOnlyList<string> WarningsOf(string action) => action switch
    {
        StorageActions.Format or StorageActions.EncryptionOff or StorageActions.EncryptionOn => [DeletesRecordings, StopsRecording],
        StorageActions.Repair => [RepairMayLose, StopsRecording],
        StorageActions.Check => [StopsRecording],
        _ => [],
    };
}
