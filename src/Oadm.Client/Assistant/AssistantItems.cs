using System.Text;

using Avalonia.Media.Imaging;

using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Client.Tasks;
using Oadm.Contracts.V1;

namespace Oadm.Client.Assistant;

/// <summary>One entry of the Assistant's conversation list (the panel's virtualized list picks a template per type).</summary>
public abstract class AssistantItem : ObservableObject;

/// <summary>The user's message, with the names of the attached files.</summary>
public sealed class AssistantUserItem(string text, IReadOnlyList<string> files) : AssistantItem
{
    public string Text { get; } = text;

    public string FilesText { get; } = string.Join(", ", files);

    public bool HasFiles => FilesText.Length > 0;
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

/// <summary>A short line per tool call ("Looking at devices"); a refused one in the error colour.</summary>
public sealed class AssistantToolItem(string text, bool failed) : AssistantItem
{
    public string Text { get; } = text;

    public bool Failed { get; } = failed;
}

/// <summary>A line of the panel itself: the notice where the data goes, Stopped, or an error (<c>done.error</c>, a refusal).</summary>
public sealed class AssistantNoticeItem(string text, bool isError = false) : AssistantItem
{
    public string Text { get; } = text;

    public bool IsError { get; } = isError;
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

/// <summary>One row of a device table: a click selects the device in the grid.</summary>
public sealed record AssistantTableRow(string DeviceId, string Name, string Address, string Model, string Status);

/// <summary>A device list from a tool: at most 50 rows, then "+N more" with Select all in the grid.</summary>
public sealed class AssistantTableItem : AssistantItem
{
    public AssistantTableItem(AssistantTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        Rows = [.. table.Rows.Select(r => new AssistantTableRow(r.DeviceId, r.Name, r.Address, r.Model, r.Status))];
        Total = Math.Max(table.Total, Rows.Count);
        AllDeviceIds = table.AllDeviceIds.Count > 0 ? [.. table.AllDeviceIds] : [.. Rows.Select(r => r.DeviceId)];
    }

    public IReadOnlyList<AssistantTableRow> Rows { get; }

    /// <summary>The most rows the table shows in the chat (no scroll inside an answer); the rest is "+N more".</summary>
    public const int MaxShownRows = 8;

    /// <summary>The rows the table shows: the first <see cref="MaxShownRows"/>.</summary>
    public IReadOnlyList<AssistantTableRow> ShownRows => Rows.Count <= MaxShownRows ? Rows : [.. Rows.Take(MaxShownRows)];

    public int Total { get; }

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
    }

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
    [NotifyPropertyChangedFor(nameof(IsReady), nameof(IsStarted), nameof(HasStatus))]
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

    /// <summary>The task ids Run returned.</summary>
    public IReadOnlyList<string> TaskIds => _taskIds;

    internal void MarkStarting()
    {
        State = PreviewState.Starting;
        StatusText = AssistantTexts.Starting;
        IsActive = true;
    }

    internal void MarkCancelled()
    {
        State = PreviewState.Cancelled;
        StatusText = AssistantTexts.Cancelled;
        IsActive = false;
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
