using System.Collections.ObjectModel;
using System.Collections.Specialized;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Client.Dialogs;
using Oadm.Client.Shell;
using Oadm.Client.Tasks;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Plugins;

namespace Oadm.Client.Assistant;

/// <summary>A file attached to the next message (uploaded through FileService.Upload); Remove takes it off again.</summary>
public sealed class AssistantAttachmentItem(string fileId, string name, long size)
{
    public string FileId { get; } = fileId;

    public string Name { get; } = name;

    public long Size { get; } = size;

    public string SizeText => Oadm.Sdk.Formatting.ByteSize.Text(Size);
}

/// <summary>
/// The Assistant panel (docs/spec/assistant.md "The panel"): a chat with the model on the right of every page, opened by
/// the title bar button. A conversation id per open panel and New chat (ended on close, New chat and sign out: nothing is
/// kept). Each message carries the device grid's selection (at most 10,000) and the attached files; the answer streams as
/// items (Markdown text, tool lines, preview cards, pictures, device tables, notices). Run of a preview is the user's click
/// only. Lives on the UI thread.
/// </summary>
public sealed partial class AssistantPanelViewModel : ObservableObject
{
    private static readonly FileType AnyFile = new("Files", "*", "application/octet-stream");

    private readonly IOadmApi _api;
    private readonly UserSession? _session;
    private readonly AssistantAvailability _availability;
    private readonly DevicesViewModel? _devices;
    private readonly TaskStore? _tasks;
    private readonly IDialogService? _dialogs;
    private readonly IClipboardService? _clipboard;
    private readonly HashSet<string> _notices = new(StringComparer.Ordinal);
    private CancellationTokenSource? _chat;
    private AssistantAnswerItem? _answer;

    public AssistantPanelViewModel(IOadmApi api, AssistantAvailability availability, UserSession? session = null, DevicesViewModel? devices = null,
        TaskStore? tasks = null, IDialogService? dialogs = null, IClipboardService? clipboard = null, ServerConnection? connection = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(availability);
        _api = api;
        _availability = availability;
        _session = session;
        _devices = devices;
        _tasks = tasks;
        _dialogs = dialogs;
        _clipboard = clipboard;
        availability.Changed += (_, _) => OnAvailabilityChanged();
        if (session is not null)
        {
            session.PermissionsChanged += (_, _) => OnAvailabilityChanged();
            session.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(UserSession.IsLoggedIn) && !session.IsLoggedIn)
                {
                    Close(); // sign out: the chat is forgotten
                }

                if (e.PropertyName is nameof(UserSession.IsLoggedIn) or nameof(UserSession.UserName))
                {
                    OnAvailabilityChanged();
                }
            };
        }

        if (connection is not null)
        {
            connection.Connected += (_, _) => _ = RefreshAsync(CancellationToken.None);
        }

        if (devices is not null)
        {
            devices.SelectedDevices.CollectionChanged += OnSelectionChanged;
        }

        Attachments.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasAttachments));
            OnPropertyChanged(nameof(AttachTip));
            AttachFileCommand.NotifyCanExecuteChanged();
        };
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasReadyPreview));
        };
    }

    /// <summary>The conversation, the user's messages and the answers (virtualized list).</summary>
    public RangeObservableCollection<AssistantItem> Items { get; } = [];

    /// <summary>Files attached to the next message.</summary>
    public ObservableCollection<AssistantAttachmentItem> Attachments { get; } = [];

    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>A preview card waits for Run: its Run is the panel's main button, Send turns secondary.</summary>
    public bool HasReadyPreview => Items.OfType<AssistantPreviewItem>().Any(p => p.IsReady);

    /// <summary>The empty panel shows the start suggestions.</summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>The title bar button shows: the assistant is on and the role may use it.</summary>
    public bool IsAvailable => _availability.IsOn && (_session is null || (_session.IsLoggedIn && _session.Has(HostPermissionKeys.AssistantUse)));

    /// <summary>The conversation of the open panel; null while it is closed.</summary>
    [ObservableProperty] public partial string? ConversationId { get; private set; }

    [ObservableProperty] public partial bool IsOpen { get; private set; }

    /// <summary>An answer streams: Stop instead of Send.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(StopCommand), nameof(SendSuggestionCommand))]
    [NotifyPropertyChangedFor(nameof(SendTip))]
    public partial bool IsStreaming { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(AttachFileCommand), nameof(SendSuggestionCommand))]
    [NotifyPropertyChangedFor(nameof(SendTip))]
    public partial bool IsUploading { get; private set; }

    /// <summary>The message box (Enter sends, Shift+Enter a new line, at most 8,000 characters).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyPropertyChangedFor(nameof(SendTip))]
    public partial string Draft { get; set; } = string.Empty;

    /// <summary>The picture shown larger over the window; null: none.</summary>
    [ObservableProperty] public partial AssistantPictureItem? Enlarged { get; private set; }

    /// <summary>"Copied." after Copy, or a problem with an attachment.</summary>
    [ObservableProperty] public partial string? Message { get; private set; }

    [ObservableProperty] public partial bool MessageIsError { get; private set; }

    /// <summary>"12 devices selected" below the box; empty without a selection.</summary>
    public string SelectionText => AssistantTexts.SelectionCount(SelectionCount);

    public bool HasSelection => SelectionCount > 0;

    public int SelectionCount => _devices?.SelectedDevices.Count ?? 0;

    /// <summary>More than 10,000 selected: the message below the box, Send off.</summary>
    public string? SelectionError => SelectionCount > AssistantTexts.MaxSelection ? AssistantTexts.TooManySelected : null;

    public bool HasSelectionError => SelectionError is not null;

    public static int MaxMessageLength => AssistantTexts.MaxMessageLength;

    /// <summary>Why Send is off.</summary>
    public string? SendTip => IsStreaming ? AssistantTexts.WaitForAnswer
        : IsUploading ? AssistantTexts.WaitForUpload
        : SelectionError ?? (Draft.Trim().Length == 0 ? AssistantTexts.EnterMessage : null);

    /// <summary>The server has it on and the role may use it (read on every connect).</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            _availability.Apply(await _api.GetAssistantSettingsAsync(ct).ConfigureAwait(true));
        }
        catch (RpcException)
        {
            _availability.Apply(null); // an older server, or none: no button
        }
    }

    /// <summary>The title bar button: opens the panel with a new conversation, or closes it.</summary>
    [RelayCommand]
    private void Toggle()
    {
        if (IsOpen)
        {
            Close();
        }
        else
        {
            Open();
        }
    }

    public void Open()
    {
        if (IsOpen || !IsAvailable)
        {
            return;
        }

        ConversationId = Guid.NewGuid().ToString();
        IsOpen = true;
    }

    /// <summary>Closes the panel and forgets the conversation (End on the server).</summary>
    [RelayCommand]
    public void Close()
    {
        EndConversation();
        IsOpen = false;
        ConversationId = null;
    }

    /// <summary>A fresh conversation: the old one ends.</summary>
    [RelayCommand]
    private void NewChat()
    {
        EndConversation();
        ConversationId = Guid.NewGuid().ToString();
    }

    private bool CanSend => !IsStreaming && !IsUploading && SelectionError is null && Draft.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private Task SendAsync() => SendTextAsync(Draft);

    private bool CanSendSuggestion(string? text) => !IsStreaming && !IsUploading && SelectionError is null;

    [RelayCommand(CanExecute = nameof(CanSendSuggestion))]
    private Task SendSuggestionAsync(string? text) => string.IsNullOrWhiteSpace(text) ? Task.CompletedTask : SendTextAsync(text);

    private async Task SendTextAsync(string text)
    {
        var conversation = ConversationId;
        text = text.Trim();
        if (conversation is null || text.Length == 0 || IsStreaming)
        {
            return;
        }

        if (text.Length > AssistantTexts.MaxMessageLength)
        {
            text = text[..AssistantTexts.MaxMessageLength];
        }

        var message = new AssistantMessage { ConversationId = conversation, Text = text };
        if (_devices is not null && SelectionError is null)
        {
            message.SelectedDeviceIds.AddRange(_devices.SelectedDevices.Select(d => d.Id));
        }

        message.Attachments.AddRange(Attachments.Select(a => new AssistantAttachment { FileId = a.FileId, Name = a.Name, Size = a.Size }));
        Items.Add(new AssistantUserItem(text, [.. Attachments.Select(a => a.Name)]));
        Attachments.Clear();
        Draft = string.Empty;
        Message = null;
        _answer = null;
        var chat = new CancellationTokenSource();
        _chat = chat;
        IsStreaming = true;
        try
        {
            await foreach (var e in _api.AssistantChatAsync(message, chat.Token).ConfigureAwait(true))
            {
                if (!ReferenceEquals(_chat, chat))
                {
                    break; // New chat or closed meanwhile
                }

                Apply(e);
            }
        }
        catch (OperationCanceledException) when (chat.IsCancellationRequested)
        {
            // closed or New chat: nothing to show
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && chat.IsCancellationRequested)
        {
            // closed or New chat
        }
        catch (RpcException ex)
        {
            if (ReferenceEquals(_chat, chat))
            {
                Items.Add(new AssistantNoticeItem(ex.Status.Detail, isError: true));
                if (ex.StatusCode is StatusCode.FailedPrecondition or StatusCode.Unimplemented or StatusCode.PermissionDenied)
                {
                    _ = RefreshAsync(CancellationToken.None); // turned off meanwhile: the button follows
                }
            }
        }
        finally
        {
            if (ReferenceEquals(_chat, chat))
            {
                _chat = null;
                _answer = null;
                IsStreaming = false;
            }

            chat.Dispose();
        }
    }

    /// <summary>One event of the answer into the list: text grows the current answer part, everything else ends it.</summary>
    private void Apply(AssistantEvent e)
    {
        switch (e.KindCase)
        {
            case AssistantEvent.KindOneofCase.Text:
                if (_answer is null)
                {
                    _answer = new AssistantAnswerItem();
                    Items.Add(_answer);
                }

                _answer.Append(e.Text);
                return;
            case AssistantEvent.KindOneofCase.Tool:
                Add(new AssistantToolItem(e.Tool.Text, e.Tool.Failed));
                return;
            case AssistantEvent.KindOneofCase.Preview:
                Add(new AssistantPreviewItem(e.Preview));
                return;
            case AssistantEvent.KindOneofCase.Picture:
                Add(new AssistantPictureItem(e.Picture.DeviceId, e.Picture.DeviceName, e.Picture.Jpeg.ToByteArray()));
                return;
            case AssistantEvent.KindOneofCase.Table:
                Add(new AssistantTableItem(e.Table) { RowChosen = row => SelectInGrid([row.DeviceId]) });
                return;
            case AssistantEvent.KindOneofCase.Notice:
                if (_notices.Add(e.Notice))
                {
                    Add(new AssistantNoticeItem(e.Notice));
                }

                return;
            case AssistantEvent.KindOneofCase.Done:
                if (e.Done.Error.Length > 0)
                {
                    Add(new AssistantNoticeItem(e.Done.Error, isError: true));
                }
                else if (e.Done.Stopped)
                {
                    Add(new AssistantNoticeItem(AssistantTexts.Stopped));
                }

                return;
            default:
                return;
        }
    }

    private void Add(AssistantItem item)
    {
        _answer = null;
        Items.Add(item);
    }

    private bool CanStop => IsStreaming;

    /// <summary>Ends the answer that streams; the conversation stays (its Chat ends with done.stopped).</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        if (ConversationId is not { } conversation)
        {
            return;
        }

        try
        {
            await _api.AssistantStopAsync(conversation, CancellationToken.None).ConfigureAwait(true);
        }
        catch (RpcException ex)
        {
            _chat?.Cancel(); // the server cannot be told: at least the panel stops listening
            ShowMessage(ex.Status.Detail, isError: true);
        }
    }

    /// <summary>Runs a preview: only this click runs it (rule 2); the card then follows the tasks.</summary>
    [RelayCommand]
    private async Task RunPreviewAsync(AssistantPreviewItem? preview)
    {
        if (preview is not { IsReady: true } || ConversationId is not { } conversation)
        {
            return;
        }

        preview.MarkStarting();
        OnPropertyChanged(nameof(HasReadyPreview));
        try
        {
            var reply = await _api.AssistantRunAsync(conversation, preview.Handle, CancellationToken.None).ConfigureAwait(true);
            preview.Follow([.. reply.TaskIds], _tasks);
        }
        catch (RpcException ex)
        {
            preview.MarkRefused(ex.Status.Detail);
        }
    }

    [RelayCommand]
    private void CancelPreview(AssistantPreviewItem? preview)
    {
        if (preview is { IsReady: true })
        {
            preview.MarkCancelled(); // the handle simply ends on the server
            OnPropertyChanged(nameof(HasReadyPreview));
        }
    }

    /// <summary>The tasks pane of the Devices page (a started preview's link).</summary>
    [RelayCommand]
    private void ShowTasks()
    {
        ShowDevicesRequested?.Invoke(this, EventArgs.Empty);
        _devices?.Tasks.ShowTasksCommand.Execute(null);
    }

    /// <summary>A row of a table: that device selected in the grid.</summary>
    [RelayCommand]
    private void SelectDevice(AssistantTableRow? row)
    {
        if (row is not null)
        {
            SelectInGrid([row.DeviceId]);
        }
    }

    /// <summary>Select all in the grid: every device of the table (at most 10,000).</summary>
    [RelayCommand]
    private void SelectAll(AssistantTableItem? table)
    {
        if (table is not null)
        {
            SelectInGrid(table.AllDeviceIds);
        }
    }

    private void SelectInGrid(IReadOnlyList<string> ids)
    {
        ShowDevicesRequested?.Invoke(this, EventArgs.Empty);
        _devices?.SelectInGrid(ids);
    }

    /// <summary>The shell shows the Devices page (a table row, Select all in the grid, Show tasks).</summary>
    public event EventHandler? ShowDevicesRequested;

    [RelayCommand]
    private async Task CopyAsync(AssistantAnswerItem? answer)
    {
        if (answer is not null && _clipboard is not null && await _clipboard.SetTextAsync(answer.Text).ConfigureAwait(true))
        {
            ShowMessage(AssistantTexts.Copied, isError: false);
        }
    }

    [RelayCommand]
    private void Enlarge(AssistantPictureItem? picture) => Enlarged = picture;

    [RelayCommand]
    private void CloseEnlarged() => Enlarged = null;

    private bool CanAttach => !IsUploading && Attachments.Count < AssistantTexts.MaxAttachments;

    /// <summary>Why Attach file is off (10 files attached).</summary>
    public string? AttachTip => Attachments.Count >= AssistantTexts.MaxAttachments ? AssistantTexts.TooManyFiles : null;

    /// <summary>Attach file: the file picker, then the upload of the task dialogs (FileService.Upload, its limits and audit).</summary>
    [RelayCommand(CanExecute = nameof(CanAttach))]
    private async Task AttachFileAsync()
    {
        if (_dialogs is null || await _dialogs.PickFilePathAsync(AssistantTexts.AttachFile, AnyFile).ConfigureAwait(true) is not { } path)
        {
            return;
        }

        await AttachAsync(path).ConfigureAwait(true);
    }

    /// <summary>Uploads a local file and attaches it to the next message.</summary>
    public async Task AttachAsync(string path)
    {
        IsUploading = true;
        Message = null;
        try
        {
            var file = await _api.UploadFileAsync(path, null, CancellationToken.None).ConfigureAwait(true);
            Attachments.Add(new AssistantAttachmentItem(file.Id, file.Name, file.Size));
        }
        catch (RpcException ex)
        {
            ShowMessage(ex.Status.Detail, isError: true);
        }
        catch (IOException ex)
        {
            ShowMessage(ex.Message, isError: true);
        }
        catch (UnauthorizedAccessException ex)
        {
            ShowMessage(ex.Message, isError: true);
        }
        finally
        {
            IsUploading = false;
        }
    }

    [RelayCommand]
    private void RemoveAttachment(AssistantAttachmentItem? attachment)
    {
        if (attachment is not null)
        {
            Attachments.Remove(attachment);
        }
    }

    private void ShowMessage(string text, bool isError)
    {
        MessageIsError = isError;
        Message = text;
    }

    /// <summary>The current conversation ends: the answer stops streaming, the list empties, End on the server.</summary>
    private void EndConversation()
    {
        if (_chat is { } chat)
        {
            _chat = null;
            chat.Cancel();
            IsStreaming = false;
        }

        _answer = null;
        foreach (var item in Items.OfType<IDisposable>())
        {
            item.Dispose();
        }

        Items.Clear();
        Attachments.Clear();
        _notices.Clear();
        Enlarged = null;
        Message = null;
        Draft = string.Empty;
        if (ConversationId is { } conversation)
        {
            _ = EndQuietlyAsync(conversation);
        }
    }

    private async Task EndQuietlyAsync(string conversation)
    {
        try
        {
            await _api.AssistantEndAsync(conversation, CancellationToken.None).ConfigureAwait(true);
        }
        catch (RpcException)
        {
            // the server forgets it after 30 minutes or with the session anyway
        }
    }

    /// <summary>
    /// The button follows at once. An open panel stays (its last answer and a refusal stay readable, its X closes it); a role
    /// that lost the permission closes it.
    /// </summary>
    private void OnAvailabilityChanged()
    {
        OnPropertyChanged(nameof(IsAvailable));
        if (IsOpen && _session is { IsLoggedIn: true } session && !session.Has(HostPermissionKeys.AssistantUse))
        {
            Close();
        }
    }

    private void OnSelectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionError));
        OnPropertyChanged(nameof(HasSelectionError));
        OnPropertyChanged(nameof(SendTip));
        SendCommand.NotifyCanExecuteChanged();
        SendSuggestionCommand.NotifyCanExecuteChanged();
    }
}
