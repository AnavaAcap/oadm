using System.Text;

using Avalonia.Media.Imaging;

using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Client.Tasks;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Client.Assistant;

/// <summary>One entry of the Assistant's conversation list (the panel's virtualized list picks a template per type).</summary>
public abstract partial class AssistantItem : ObservableObject
{
    /// <summary>The end of the last answer: Try again shows here (the last answer part, or its error or Stopped line).</summary>
    [ObservableProperty] public partial bool ShowsRetry { get; internal set; }
}

/// <summary>The user's message, with the attached files (sent again by Try again, back in the box by Edit).</summary>
public sealed partial class AssistantUserItem(string text, IReadOnlyList<AssistantAttachmentItem> files, IReadOnlyList<string>? mentioned = null,
    string? mentionText = null) : AssistantItem
{
    public string Text { get; } = text;

    /// <summary>The devices chosen with @ that went with the message.</summary>
    public IReadOnlyList<string> Mentioned { get; } = mentioned ?? [];

    /// <summary>"3 devices", or the device's address when one.</summary>
    public string MentionText { get; } = mentionText ?? string.Empty;

    public bool HasMention => Mentioned.Count > 0;

    public IReadOnlyList<AssistantAttachmentItem> Files { get; } = files;

    public string FilesText { get; } = string.Join(", ", files.Select(f => f.Name));

    public bool HasFiles => FilesText.Length > 0;

    /// <summary>The files as small cards below the bubble (icon, name, size).</summary>
    public bool ShowsFiles => Files.Count > 0;

    /// <summary>The server answered this message (it is in the server's conversation): Try again and Edit replace it there.</summary>
    public bool Reached { get; internal set; }

    /// <summary>The last message of the user while no answer streams: Edit shows on hover.</summary>
    [ObservableProperty] public partial bool CanEdit { get; internal set; }
}

/// <summary>
/// The live status line of an answer that runs (a small ring and one line): "Thinking" from Send, then the line of the tool
/// that runs ("Looking at devices"). It sits at the end of the list while the answer waits for something other than text;
/// streamed text takes its place, the next tool brings it back.
/// </summary>
public sealed partial class AssistantStatusItem : AssistantItem
{
    [ObservableProperty] public partial string Text { get; internal set; } = AssistantTexts.Thinking;
}

/// <summary>
/// The tool lines of one answer, folded: once the answer ended, one row "Steps (5)" at its top with a chevron that opens the
/// list. A failed step keeps its red mark; the list is open by itself only when the answer failed.
/// </summary>
public sealed partial class AssistantStepsItem : AssistantItem
{
    private readonly List<AssistantToolItem> _steps = [];

    public IReadOnlyList<AssistantToolItem> Steps => _steps;

    public int Count => _steps.Count;

    /// <summary>"Steps (5)".</summary>
    public string Header => AssistantTexts.Steps(_steps.Count);

    public bool HasFailed => _steps.Any(s => s.Failed);

    [ObservableProperty] public partial bool IsExpanded { get; set; }

    internal void Add(AssistantToolItem step) => _steps.Add(step);
}

/// <summary>
/// A part of the model's answer, as Markdown; grows while it streams. Pieces collect in a buffer (<see cref="Append"/>) and
/// reach <see cref="Text"/> (and the view's Markdown) only on <see cref="Flush"/>: the panel flushes at most every 100 ms
/// while it streams and once at the end. Copy copies its text.
/// </summary>
public sealed partial class AssistantAnswerItem : AssistantItem
{
    private readonly StringBuilder _text = new();

    [ObservableProperty]
    public partial string Text { get; private set; } = string.Empty;

    /// <summary>Text not shown yet.</summary>
    public bool HasPending => _text.Length != Text.Length;

    public void Append(string piece) => _text.Append(piece);

    /// <summary>Shows what arrived since the last flush (one change of <see cref="Text"/>); nothing new: nothing.</summary>
    public void Flush()
    {
        if (HasPending)
        {
            Text = _text.ToString();
        }
    }
}

/// <summary>A short line per tool call ("Looking at devices"), a step of <see cref="AssistantStepsItem"/>; a refused one in the error colour.</summary>
public sealed class AssistantToolItem(string text, bool failed)
{
    public string Text { get; } = text;

    public bool Failed { get; } = failed;
}

/// <summary>A line of the panel itself: the notice where the data goes, Stopped, or an error (<c>done.error</c>, a refusal).</summary>
public sealed class AssistantNoticeItem(string text, bool isError = false, bool isStopped = false) : AssistantItem
{
    public string Text { get; } = text;

    public bool IsError { get; } = isError;

    /// <summary>The line after Stop.</summary>
    public bool IsStopped { get; } = isStopped;
}

/// <summary>
/// A camera picture: shown small in the answer, a click shows it larger. Never stored. Only the JPEG stays with the chat:
/// the list decodes a picture at the panel's width when it shows the entry (<see cref="Thumbnail"/>), the enlarged view
/// decodes the full size (<see cref="Decode"/>) and lets it go when it closes.
/// </summary>
public sealed class AssistantPictureItem(string deviceId, string deviceName, byte[] jpeg) : AssistantItem
{
    /// <summary>The width the list decodes to (the panel's content width).</summary>
    public const int ThumbnailWidth = 560;

    public string DeviceId { get; } = deviceId;

    public string DeviceName { get; } = deviceName;

    public byte[] Jpeg { get; } = jpeg;

    /// <summary>A new bitmap of at most <see cref="ThumbnailWidth"/> pixels wide for the list (not kept here); null when it cannot be read.</summary>
    public Bitmap? Thumbnail => Decode(ThumbnailWidth);

    /// <summary>A new bitmap of the picture, full size or at most <paramref name="width"/> wide; the caller owns it.</summary>
    public Bitmap? Decode(int? width = null)
    {
        try
        {
            using var stream = new MemoryStream(Jpeg);
            return width is { } w ? Bitmap.DecodeToWidth(stream, w) : new Bitmap(stream);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>The chips of what an answer used (the server's sources: "7 devices", "Compliance", "Docs") under the label SOURCES.</summary>
public sealed class AssistantSourcesItem(IReadOnlyList<string> chips) : AssistantItem
{
    public IReadOnlyList<string> Chips { get; } = chips;
}

/// <summary>
/// The model's next questions (1 to 3) under the label NEXT: a click sends one as the user's message. Plain text, never
/// instructions; only the newest answer keeps them (a new message takes them away).
/// </summary>
public sealed class AssistantFollowUpsItem(IReadOnlyList<string> questions) : AssistantItem
{
    public IReadOnlyList<string> Questions { get; } = questions;
}

/// <summary>One row of a device table: a click selects the device in the grid.</summary>
public sealed record AssistantTableRow(string DeviceId, string Name, string Address, string Model, string Status)
{
    /// <summary>The 4th column: the fact the question filtered by (<see cref="AssistantTableItem.FourthHeader"/>), else the status.</summary>
    public string Fourth { get; init; } = Status;
}

/// <summary>A device list from a tool: at most 50 rows, then "+N more" with Select all in the grid.</summary>
public sealed class AssistantTableItem : AssistantItem
{
    public AssistantTableItem(AssistantTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var detail = table.DetailHeader.Length > 0;
        FourthHeader = detail ? table.DetailHeader : AssistantTexts.ColumnStatus;
        Rows = [.. table.Rows.Select(r => new AssistantTableRow(r.DeviceId, r.Name, r.Address, r.Model, r.Status) { Fourth = detail ? r.Detail : r.Status })];
        Total = Math.Max(table.Total, Rows.Count);
        AllDeviceIds = table.AllDeviceIds.Count > 0 ? [.. table.AllDeviceIds] : [.. Rows.Select(r => r.DeviceId)];
    }

    public IReadOnlyList<AssistantTableRow> Rows { get; }

    /// <summary>The header of the 4th column: the fact the question filtered by ("Clock", "Vulnerabilities"), else Status.</summary>
    public string FourthHeader { get; }

    /// <summary>The most rows the table shows in the chat (no scroll inside an answer); the rest is "+N more".</summary>
    public const int MaxShownRows = 8;

    /// <summary>The rows the table shows: the first <see cref="MaxShownRows"/>.</summary>
    public IReadOnlyList<AssistantTableRow> ShownRows => Rows.Count <= MaxShownRows ? Rows : [.. Rows.Take(MaxShownRows)];

    public int Total { get; }

    /// <summary>The card's title: "60 devices".</summary>
    public string Title => AssistantTexts.Devices(Total);

    /// <summary>The first result card of an answer shows the label RESULTS above it.</summary>
    public bool ShowsLabel { get; internal set; }

    public IReadOnlyList<string> AllDeviceIds { get; }

    public bool HasMore => Total > ShownRows.Count;

    public string MoreText => AssistantTexts.More(Total - ShownRows.Count);

    /// <summary>The panel's action for a clicked row (the device selected in the grid).</summary>
    internal Action<AssistantTableRow>? RowChosen { get; set; }

    /// <summary>The row clicked in the table (bound to the table's selection): selects that device in the grid.</summary>
    public AssistantTableRow? SelectedRow
    {
        get;
        set
        {
            if (SetProperty(ref field, value) && value is not null)
            {
                RowChosen?.Invoke(value);
            }
        }
    }
}

/// <summary>The state of a preview card.</summary>
public enum PreviewState
{
    /// <summary>Run and Cancel.</summary>
    Ready,

    /// <summary>Run was pressed, the server starts the tasks.</summary>
    Starting,

    /// <summary>The tasks run or ended; the card follows them.</summary>
    Started,

    /// <summary>Cancel was pressed.</summary>
    Cancelled,

    /// <summary>Run was refused (the preview ended); the reason shows.</summary>
    Refused,
}

/// <summary>
/// A secret field of a preview card (docs/spec/mcp.md "Secrets"): a password the run needs, typed by the user into the card and
/// sent with Run straight to the server (<c>AssistantRunRequest.secrets</c>), never into the chat or to the model. Its error
/// shows below it once it was edited, left or Run was tried.
/// </summary>
public sealed partial class AssistantSecretFieldItem : ValidatingViewModel
{
    public AssistantSecretFieldItem(string name, string label)
    {
        Name = name;
        Label = Contracts.Assistant.AssistantTexts.SecretLabel(label);
        EmptyError = Contracts.Assistant.AssistantTexts.EnterSecret(label);
        Validation.Rule(nameof(Value), () => string.IsNullOrEmpty(Value) ? EmptyError : null);
        Validation.Validate();
    }

    /// <summary>The key of the value in Run's secrets.</summary>
    public string Name { get; }

    /// <summary>"Password".</summary>
    public string Label { get; }

    /// <summary>"Enter the password."</summary>
    public string EmptyError { get; }

    [ObservableProperty]
    public partial string Value { get; set; } = string.Empty;

    partial void OnValueChanged(string value) => Validation.Validate();

    /// <summary>Shows the field's error (Run was tried); <paramref name="serverError"/> is the server's refusal of this field.</summary>
    internal void ShowError(string? serverError = null)
    {
        if (serverError is not null)
        {
            Validation.SetServerError(nameof(Value), serverError);
        }

        Validation.ShowAll();
    }

    /// <summary>Forgets the value (the run started or the card ended): it never stays in the client longer than needed.</summary>
    internal void Clear()
    {
        Value = string.Empty;
        Validation.Reset(nameof(Value));
    }
}

/// <summary>
/// A file a tool answer offered (docs/spec/mcp.md "Files"): a card with its name, size and Save. Save asks where to keep it
/// and fetches it then (<c>AssistantService.Download</c>, the user's rights of then); the bytes never pass the model.
/// </summary>
public sealed partial class AssistantFileItem(AssistantFile file) : AssistantItem
{
    public string FileRef { get; } = file?.FileRef ?? string.Empty;

    public string Name { get; } = file?.Name ?? string.Empty;

    public string ContentType { get; } = file?.ContentType ?? string.Empty;

    /// <summary>"48 KB"; empty when the size is not known before the download.</summary>
    public string SizeText { get; } = file is { Size: >= 0 } ? Oadm.Sdk.Formatting.ByteSize.Text(file.Size) : string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string StatusText { get; internal set; } = string.Empty;

    public bool HasStatus => StatusText.Length > 0;

    [ObservableProperty] public partial bool IsSaving { get; internal set; }

    [ObservableProperty] public partial bool HasFailed { get; internal set; }

    [ObservableProperty] public partial bool IsSaved { get; internal set; }
}

/// <summary>
/// The model found no OADM function and offers to look in the Axis docs online (docs/spec/assistant.md "Looking in the Axis
/// docs"): the question with Not now and Look up online. Only Look up online sends the offer's id with the next message; Not
/// now, another message or the offer's end leave it unused (the server forgets it).
/// </summary>
public sealed partial class AssistantDocsOfferItem(AssistantDocsOffer offer) : AssistantItem
{
    public string OfferId { get; } = offer?.OfferId ?? string.Empty;

    /// <summary>What the model would look up (data from the model, shown as written).</summary>
    public string Topic { get; } = offer?.Topic ?? string.Empty;

    /// <summary>The card waits for the user's answer: its two buttons show.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial bool IsReady { get; private set; } = true;

    /// <summary>The newest card that waits: its Look up online is the panel's main button (one accent per view).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LookUpIsPrimary), nameof(LookUpIsSecondary))]
    public partial bool IsNewestReady { get; internal set; }

    public bool LookUpIsPrimary => IsNewestReady;

    public bool LookUpIsSecondary => !IsNewestReady;

    /// <summary>The user said yes: the panel sends the lookup.</summary>
    [ObservableProperty] public partial bool IsAccepted { get; private set; }

    [ObservableProperty] public partial string StatusText { get; private set; } = string.Empty;

    public bool HasStatus => !IsReady;

    internal void MarkAccepted()
    {
        IsAccepted = true;
        StatusText = AssistantTexts.LookedUpOnline;
        IsReady = false;
    }

    internal void MarkDeclined()
    {
        StatusText = AssistantTexts.NotLookedUp;
        IsReady = false;
    }
}

/// <summary>
/// A change the model prepared (docs/spec/assistant.md rule 2): title, device count, the first devices, the skipped ones and
/// the warnings; only the user's Run runs it. After Run the card follows its tasks in the client's task mirror
/// (<see cref="TaskStore"/>) and links to the tasks pane.
/// </summary>
public sealed partial class AssistantPreviewItem : AssistantItem, IDisposable
{
    private readonly Dictionary<string, TaskRowViewModel> _gone = new(StringComparer.Ordinal);
    private TaskStore? _tasks;
    private IReadOnlyList<string> _taskIds = [];
    private HashSet<string> _taskSet = new(StringComparer.Ordinal);

    public AssistantPreviewItem(AssistantPreviewCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        Handle = card.Handle;
        Title = card.Title;
        DeviceCount = card.DeviceCount;
        CountText = card.CountText.Length > 0 ? card.CountText : AssistantTexts.Devices(card.DeviceCount);
        FirstDevices = [.. card.FirstDevices];
        Skipped = [.. card.Skipped.Select(AssistantTexts.Skipped)];
        Warnings = [.. card.Warnings];
        Details = [.. card.Details];
        IsDestructive = card.Destructive;
        Secrets = [.. card.Secrets.Select(s => new AssistantSecretFieldItem(s.Name, s.Label))];
        foreach (var secret in Secrets)
        {
            secret.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(AssistantSecretFieldItem.Value) or nameof(AssistantSecretFieldItem.HasErrors))
                {
                    OnPropertyChanged(nameof(CanRun));
                    OnPropertyChanged(nameof(RunBlockedReason));
                }
            };
        }
    }

    /// <summary>The secrets Run needs (a password): typed into the card, sent with Run, never into the chat.</summary>
    public IReadOnlyList<AssistantSecretFieldItem> Secrets { get; }

    public bool HasSecrets => Secrets.Count > 0;

    /// <summary>The secret fields show while the card waits for Run (afterwards their values are gone).</summary>
    public bool ShowsSecrets => HasSecrets && IsReady;

    /// <summary>Run is possible: the card waits for it and every secret field has a value.</summary>
    public bool CanRun => IsReady && Secrets.All(s => s.Value.Length > 0);

    /// <summary>Why Run is disabled ("Enter the password."); null when it is not.</summary>
    public string? RunBlockedReason => !IsReady ? null : Secrets.FirstOrDefault(s => s.Value.Length == 0)?.EmptyError;

    /// <summary>The values for Run by name.</summary>
    internal Dictionary<string, string> SecretValues() => Secrets.ToDictionary(s => s.Name, s => s.Value, StringComparer.Ordinal);

    /// <summary>What Run does, one line per key field, from the server's preview ("Sends to: http://10.0.0.5/hook").</summary>
    public IReadOnlyList<string> Details { get; }

    public bool HasDetails => Details.Count > 0;

    /// <summary>Run deletes or removes: its button is red.</summary>
    public bool IsDestructive { get; }

    /// <summary>The newest card that waits for Run: its Run is the panel's main button; older ones are secondary.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunIsPrimary), nameof(RunIsSecondary))]
    public partial bool IsNewestReady { get; internal set; }

    public bool RunIsPrimary => IsNewestReady && !IsDestructive;

    public bool RunIsSecondary => !IsNewestReady && !IsDestructive;

    public string Handle { get; }

    public string Title { get; }

    public int DeviceCount { get; }

    /// <summary>"3 devices", "2 tasks", "1 rule" (the server's unit; without one the device count).</summary>
    public string CountText { get; }

    public IReadOnlyList<string> FirstDevices { get; }

    /// <summary>The first devices in one line, with "+N more" when the card has more devices.</summary>
    public string DevicesText => FirstDevices.Count == 0 ? string.Empty
        : string.Join(", ", FirstDevices) + (DeviceCount > FirstDevices.Count ? ", " + AssistantTexts.More(DeviceCount - FirstDevices.Count) : string.Empty);

    public bool HasDevices => FirstDevices.Count > 0;

    public IReadOnlyList<string> Skipped { get; }

    public bool HasSkipped => Skipped.Count > 0;

    public IReadOnlyList<string> Warnings { get; }

    public bool HasWarnings => Warnings.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady), nameof(IsStarted), nameof(HasStatus), nameof(CanRun), nameof(RunBlockedReason), nameof(ShowsSecrets))]
    public partial PreviewState State { get; private set; }

    public bool IsReady => State == PreviewState.Ready;

    public bool IsStarted => State == PreviewState.Started;

    /// <summary>"Running: 3 of 12 ended", "Done on 12 devices", "Cancelled.", or why Run was refused.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string StatusText { get; private set; } = string.Empty;

    public bool HasStatus => StatusText.Length > 0;

    [ObservableProperty] public partial bool IsActive { get; private set; }

    [ObservableProperty] public partial bool HasFailed { get; private set; }

    [ObservableProperty] public partial bool IsDone { get; private set; }

    /// <summary>The devices whose tasks failed, once every task ended: Try the failed devices again.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRetryFailed))]
    public partial IReadOnlyList<string> FailedDeviceIds { get; private set; } = [];

    /// <summary>Try the failed devices again was pressed (a new card follows): the button goes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRetryFailed))]
    public partial bool RetriedFailed { get; internal set; }

    public bool CanRetryFailed => FailedDeviceIds.Count > 0 && !RetriedFailed;

    /// <summary>The task ids Run returned.</summary>
    public IReadOnlyList<string> TaskIds => _taskIds;

    internal void MarkStarting()
    {
        State = PreviewState.Starting;
        StatusText = AssistantTexts.Starting;
        IsActive = true;
    }

    /// <summary>The server refused Run because of a secret field: the card waits again with the error below that field.</summary>
    internal void MarkSecretRefused(string field, string error)
    {
        State = PreviewState.Ready;
        StatusText = string.Empty;
        IsActive = false;
        (Secrets.FirstOrDefault(s => s.Name == field) ?? (Secrets.Count > 0 ? Secrets[0] : null))?.ShowError(error);
    }

    internal void MarkCancelled()
    {
        State = PreviewState.Cancelled;
        StatusText = AssistantTexts.Cancelled;
        IsActive = false;
        ClearSecrets();
    }

    /// <summary>The typed secrets are forgotten once they went to the server or the card ended.</summary>
    internal void ClearSecrets()
    {
        foreach (var secret in Secrets)
        {
            secret.Clear();
        }
    }

    internal void MarkRefused(string reason)
    {
        State = PreviewState.Refused;
        StatusText = reason;
        IsActive = false;
        HasFailed = true;
    }

    /// <summary>Follows the tasks of the run in the task mirror until all ended (a task that left the mirror ended for the card).</summary>
    internal void Follow(IReadOnlyList<string> taskIds, TaskStore? tasks)
    {
        _taskIds = taskIds;
        _taskSet = new HashSet<string>(taskIds, StringComparer.Ordinal);
        State = PreviewState.Started;
        _tasks = tasks;
        if (tasks is not null)
        {
            tasks.Changed += OnTasksChanged;
            tasks.Forgotten += OnTaskForgotten;
        }

        Recount();
    }

    private void OnTaskForgotten(object? sender, TaskRowViewModel row)
    {
        if (_taskSet.Contains(row.Id))
        {
            _gone[row.Id] = row;
        }
    }

    private void OnTasksChanged(object? sender, TaskRowViewModel? row)
    {
        if (row is null || _taskSet.Contains(row.Id))
        {
            Recount();
        }
    }

    private void Recount()
    {
        int done = 0, failed = 0, cancelled = 0, active = 0;
        foreach (var id in _taskIds)
        {
            var row = _tasks?.Find(id) ?? _gone.GetValueOrDefault(id);
            var state = row is null ? TaskState.Queued : _gone.ContainsKey(id) && row.IsActive ? TaskState.Cancelled : row.State;
            switch (state)
            {
                case TaskState.Done or TaskState.DoneWithWarnings:
                    done++;
                    break;
                case TaskState.Failed:
                    failed++;
                    break;
                case TaskState.Cancelled:
                    cancelled++;
                    break;
                default:
                    active++;
                    break;
            }
        }

        IsActive = active > 0;
        HasFailed = failed > 0;
        IsDone = active == 0 && failed == 0;
        StatusText = AssistantTexts.RunState(_taskIds.Count, done, failed, cancelled, active);
        if (active == 0 && failed > 0)
        {
            // The end with failures: the reasons, the most frequent first, and the devices to try again.
            var failedRows = _taskIds.Select(id => _tasks?.Find(id) ?? _gone.GetValueOrDefault(id)).Where(r => r is { State: TaskState.Failed }).Select(r => r!).ToList();
            var reasons = failedRows.GroupBy(r => r.StatusMessage.Length > 0 ? r.StatusMessage : AssistantTexts.FailedReason, StringComparer.Ordinal)
                .Select(g => (Reason: g.Key, Count: g.Count())).OrderByDescending(r => r.Count).ThenBy(r => r.Reason, StringComparer.CurrentCulture).ToList();
            StatusText = AssistantTexts.RunResult(done, failed, cancelled, reasons);
            FailedDeviceIds = [.. failedRows.Select(r => r.DeviceId).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal)];
        }
        if (active == 0)
        {
            Dispose();
        }
    }

    public void Dispose()
    {
        if (_tasks is { } tasks)
        {
            tasks.Changed -= OnTasksChanged;
            tasks.Forgotten -= OnTaskForgotten;
            _tasks = null;
        }
    }
}
