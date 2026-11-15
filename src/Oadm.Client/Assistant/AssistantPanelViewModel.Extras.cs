using System.Text;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Oadm.Client.Controls;
using Oadm.Client.Devices;
using Oadm.Client.Dialogs;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Plugins;

namespace Oadm.Client.Assistant;

/// <summary>A live count of the empty panel ("12 offline"); a click asks <see cref="Question"/>.</summary>
public sealed record AssistantStartChip(string Text, string Question);

/// <summary>A start suggestion of the empty panel: an icon and one line, a click sends <see cref="Text"/>.</summary>
public sealed record AssistantStartCard(string IconKey, string Text);

/// <summary>
/// The Assistant panel's second part (docs/spec/assistant.md "The panel"): the empty panel's counts and suggestions, the
/// devices chosen with @ (the shared device picker), device links in answers with their menu, Copy as CSV and Save as CSV,
/// Copy chat, Try the failed devices again, and Ctrl+J.
/// </summary>
public sealed partial class AssistantPanelViewModel : IMarkdownActions
{
    /// <summary>The most rows a device table's CSV holds.</summary>
    public const int MaxCsvRows = AssistantTexts.MaxSelection;

    /// <summary>The empty panel shows at most this many suggestions.</summary>
    public const int MaxStartCards = 4;

    private static readonly FileType CsvFile = new("CSV file", "csv", "text/csv");

    private AssistantDeviceIndex? _index;

    /// <summary>The counts and links of the device store (null without a device grid).</summary>
    public AssistantDeviceIndex? DeviceIndex => _index;

    private void InitializeExtras()
    {
        if (_devices is not null)
        {
            _index = new AssistantDeviceIndex(_devices.Store);
            _index.Changed += (_, _) => UpdateStart();
        }

        if (_session is not null)
        {
            _session.PermissionsChanged += (_, _) => UpdateStart();
        }

        UpdateStart();
    }

    partial void OnIsOpenChanged(bool value)
    {
        if (_index is not null)
        {
            _index.IsActive = value; // counted while the panel is open only
        }

        UpdateStart();
    }

    // ---- The empty panel -----------------------------------------------------------------------------------------------

    /// <summary>The page the user is on (its rail key, e.g. "devices", "plugin:oadm.pki"): the suggestions follow it.</summary>
    [ObservableProperty] public partial string? CurrentPageKey { get; set; }

    partial void OnCurrentPageKeyChanged(string? value) => UpdateStart();

    /// <summary>The live counts (only the ones above zero).</summary>
    [ObservableProperty] public partial IReadOnlyList<AssistantStartChip> StartChips { get; private set; } = [];

    /// <summary>At most four suggestions for the moment: the selection, the page, then the general ones.</summary>
    [ObservableProperty] public partial IReadOnlyList<AssistantStartCard> StartCards { get; private set; } = [];

    public bool HasStartChips => StartChips.Count > 0;

    partial void OnStartChipsChanged(IReadOnlyList<AssistantStartChip> value) => OnPropertyChanged(nameof(HasStartChips));

    /// <summary>A role that may restart devices gets change suggestions (no change suggestions without task rights).</summary>
    private bool MayRunTasks => _session is null || _session.Has(PermissionKeys.Task(RestartTask));

    private const string RestartTask = "oadm.restart";

    /// <summary>The chips and cards again (O(1): the counts are kept per change, the lists are short).</summary>
    private void UpdateStart()
    {
        var chips = new List<AssistantStartChip>();
        if (_index is { IsActive: true } index)
        {
            if (index.Total > 0)
            {
                chips.Add(new AssistantStartChip(AssistantTexts.DevicesChip(index.Total), AssistantTexts.AskSummary));
            }

            if (index.Offline > 0)
            {
                chips.Add(new AssistantStartChip(AssistantTexts.OfflineChip(index.Offline), AssistantTexts.AskOffline));
            }

            if (index.Vulnerable > 0)
            {
                chips.Add(new AssistantStartChip(AssistantTexts.VulnerableChip(index.Vulnerable), AssistantTexts.AskVulnerable));
            }
        }

        if (!chips.SequenceEqual(StartChips))
        {
            StartChips = chips;
        }

        var cards = new List<AssistantStartCard>();
        void Add(string icon, string text)
        {
            if (cards.Count < MaxStartCards && !cards.Any(c => c.Text == text))
            {
                cards.Add(new AssistantStartCard(icon, text));
            }
        }

        int selected = SelectionCount;
        if (selected > 0 && SelectionError is null)
        {
            if (MayRunTasks)
            {
                Add("restart", AssistantTexts.RestartSelected(selected));
            }

            if (selected == 1)
            {
                Add("snapshot", AssistantTexts.PictureOfSelected);
            }

            Add("firmware", AssistantTexts.FirmwareOfSelected);
        }

        foreach (var (icon, text) in PageSuggestions(CurrentPageKey))
        {
            Add(icon, text);
        }

        Add("devices", AssistantTexts.AskOffline);
        Add("shieldAlert", AssistantTexts.AskVulnerable);
        Add("firmware", AssistantTexts.OldFirmware);
        Add("snapshot", AssistantTexts.PictureOfACamera);
        if (!cards.SequenceEqual(StartCards))
        {
            StartCards = cards;
        }
    }

    /// <summary>A short list per page; none for a page without its own questions.</summary>
    private static IEnumerable<(string Icon, string Text)> PageSuggestions(string? page) => page switch
    {
        HostPages.Devices => [("tasks", AssistantTexts.RecentTasks), ("firmware", AssistantTexts.OldFirmware)],
        "plugin:oadm.pki" => [("shield", AssistantTexts.ExpiringCertificates), ("lock", AssistantTexts.HttpsOff)],
        HostPages.EventRules => [("zap", AssistantTexts.RulesOn), ("zap", AssistantTexts.RulesFailing)],
        HostPages.Compliance => [("squareActivity", AssistantTexts.ComplianceFailing)],
        HostPages.Sites => [("server", AssistantTexts.SitesOffline)],
        HostPages.Logs => [("logs", AssistantTexts.LogProblems)],
        _ => [],
    };

    // ---- Devices chosen with @ -----------------------------------------------------------------------------------------

    /// <summary>The devices chosen with @ or Choose devices for the next message (one chip in the box).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMention), nameof(MentionText))]
    public partial IReadOnlyList<string> Mentioned { get; private set; } = [];

    public bool HasMention => Mentioned.Count > 0;

    /// <summary>"3 devices", or the device's address when one.</summary>
    public string MentionText => AssistantTexts.ChosenDevices(Mentioned.Count, Mentioned.Count == 1 ? _devices?.Store.Find(Mentioned[0])?.DisplayAddress : null);

    private bool CanChooseDevices => _devices is not null && _dialogs is not null;

    /// <summary>@ in the box, the devices button or a click on the chip: the shared device picker with the current choice.</summary>
    [RelayCommand(CanExecute = nameof(CanChooseDevices))]
    private async Task ChooseDevicesAsync()
    {
        if (_devices is null || _dialogs is null)
        {
            return;
        }

        var chosen = Mentioned.Select(id => Guid.TryParse(id, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty);
        var picker = new DevicePickerViewModel(_devices.Store.Devices, chosen);
        if (await _dialogs.ShowDevicePickerAsync(picker).ConfigureAwait(true))
        {
            var ids = picker.SelectedIds.Select(g => g.ToString()).ToList();
            Mentioned = [.. ids.Select(id => _devices.Store.Find(id)?.Id ?? id)];
        }

        FocusMessageRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void RemoveMention() => Mentioned = [];

    // ---- Device links in answers (IMarkdownActions) ---------------------------------------------------------------------

    IReadOnlyList<MarkdownLink> IMarkdownActions.FindLinks(string text) =>
        _index is null ? [] : [.. _index.Find(text).Select(m => new MarkdownLink(m.Start, m.Length, m.DeviceId))];

    /// <summary>A click on a device in an answer: selected in the grid.</summary>
    void IMarkdownActions.OpenLink(string key) => SelectInGrid([key]);

    /// <summary>The link's menu: Select, Open live view, Open web interface (each as the role allows, like the grid's menu).</summary>
    IReadOnlyList<MarkdownLinkAction> IMarkdownActions.LinkMenu(string key)
    {
        var actions = new List<MarkdownLinkAction> { new(AssistantTexts.Select, "check", () => SelectInGrid([key])) };
        if (_devices?.Store.Find(key) is not { } row)
        {
            return actions;
        }

        if (_devices.LiveView.IsAllowed)
        {
            actions.Add(new(AssistantTexts.OpenLiveView, "video", () =>
            {
                SelectInGrid([key]);
                _devices.LiveView.Open(row);
            }));
        }

        if (_devices.Allows(HostPermissionKeys.DevicesWebInterface))
        {
            actions.Add(new(AssistantTexts.OpenWebInterface, "externalLink", () => _devices.OpenWebUiCommand.Execute(row)));
        }

        return actions;
    }

    void IMarkdownActions.CopyTable(MarkdownTable table) => _ = CopyTextAsync(MarkdownDocument.ToCsv(table));

    void IMarkdownActions.SaveTable(MarkdownTable table) => _ = SaveCsvAsync(MarkdownDocument.ToCsv(table));

    // ---- CSV of device tables ------------------------------------------------------------------------------------------

    /// <summary>The role may export devices (like Export devices): Copy as CSV and Save as CSV of a device table.</summary>
    public bool CanExportTables => _devices?.Allows(HostPermissionKeys.DevicesExport) ?? true;

    /// <summary>
    /// A device table as CSV: every device of the table the client knows (up to 10,000) in the columns of Export devices;
    /// without the device grid the table's own rows.
    /// </summary>
    public string TableCsv(AssistantTableItem table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (_devices is not null)
        {
            var rows = table.AllDeviceIds.Take(MaxCsvRows).Select(_devices.Store.Find).OfType<DeviceRowViewModel>().ToList();
            if (rows.Count > 0)
            {
                return DeviceListCsv.Write(rows);
            }
        }

        var csv = new StringBuilder();
        Infrastructure.Csv.AppendRecord(csv, [AssistantTexts.ColumnName, AssistantTexts.ColumnAddress, AssistantTexts.ColumnModel, AssistantTexts.ColumnStatus]);
        foreach (var row in table.Rows)
        {
            Infrastructure.Csv.AppendRecord(csv, [row.Name, row.Address, row.Model, row.Status]);
        }

        return csv.ToString();
    }

    [RelayCommand]
    private Task CopyTableCsvAsync(AssistantTableItem? table) => table is null || !CanExportTables ? Task.CompletedTask : CopyTextAsync(TableCsv(table));

    [RelayCommand]
    private Task SaveTableCsvAsync(AssistantTableItem? table) => table is null || !CanExportTables ? Task.CompletedTask : SaveCsvAsync(TableCsv(table));

    private async Task CopyTextAsync(string text)
    {
        if (_clipboard is not null && await _clipboard.SetTextAsync(text).ConfigureAwait(true))
        {
            ShowMessage(AssistantTexts.Copied, isError: false);
        }
    }

    private async Task SaveCsvAsync(string csv)
    {
        if (_dialogs is null)
        {
            return;
        }

        byte[] content = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv)];
        await _dialogs.SaveFileAsync(AssistantTexts.SaveCsv, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"oadm-assistant-{_time.GetLocalNow():yyyy-MM-dd}.csv"),
            CsvFile, content).ConfigureAwait(true);
    }

    // ---- Copy chat -----------------------------------------------------------------------------------------------------

    private bool CanCopyChat => !IsEmpty;

    /// <summary>The whole conversation as Markdown on the clipboard (questions, answers, tool lines, card summaries).</summary>
    [RelayCommand(CanExecute = nameof(CanCopyChat))]
    private Task CopyChatAsync() => IsEmpty ? Task.CompletedTask : CopyTextAsync(ChatMarkdown());

    /// <summary>
    /// The conversation as Markdown: each question, answer, tool line, notice, table (its rows), picture (the device's name)
    /// and card (title, count, what Run does, the state). Only what the panel shows: no secret ever reaches the panel.
    /// </summary>
    public string ChatMarkdown()
    {
        var md = new StringBuilder();
        foreach (var item in Items)
        {
            switch (item)
            {
                case AssistantUserItem user:
                    md.Append("**You:** ").AppendLine(user.Text);
                    if (user.HasFiles)
                    {
                        md.Append("Files: ").AppendLine(user.FilesText);
                    }

                    if (user.HasMention)
                    {
                        md.Append("Devices: ").AppendLine(user.MentionText);
                    }

                    break;
                case AssistantAnswerItem answer:
                    md.AppendLine(answer.Text.TrimEnd());
                    break;
                case AssistantToolItem tool:
                    md.Append('_').Append(tool.Text).AppendLine("_");
                    break;
                case AssistantNoticeItem notice:
                    md.AppendLine(notice.Text);
                    break;
                case AssistantPictureItem picture:
                    md.Append("(A picture of ").Append(picture.DeviceName).AppendLine(")");
                    break;
                case AssistantTableItem table:
                    md.Append("| ").Append(AssistantTexts.ColumnName).Append(" | ").Append(AssistantTexts.ColumnAddress).Append(" | ")
                        .Append(AssistantTexts.ColumnModel).Append(" | ").Append(AssistantTexts.ColumnStatus).AppendLine(" |");
                    md.AppendLine("|---|---|---|---|");
                    foreach (var row in table.Rows)
                    {
                        md.Append("| ").Append(Cell(row.Name)).Append(" | ").Append(Cell(row.Address)).Append(" | ").Append(Cell(row.Model)).Append(" | ")
                            .Append(Cell(row.Status)).AppendLine(" |");
                    }

                    if (table.Total > table.Rows.Count)
                    {
                        md.AppendLine(AssistantTexts.More(table.Total - table.Rows.Count));
                    }

                    break;
                case AssistantPreviewItem card:
                    md.Append("**").Append(card.Title).Append("** (").Append(card.CountText).AppendLine(")");
                    foreach (var line in card.Details)
                    {
                        md.Append("- ").AppendLine(line);
                    }

                    if (card.HasStatus)
                    {
                        md.AppendLine(card.StatusText);
                    }

                    break;
                default:
                    continue;
            }

            md.AppendLine();
        }

        return md.ToString().TrimEnd() + Environment.NewLine;

        static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal);
    }

    // ---- Try the failed devices again ----------------------------------------------------------------------------------

    /// <summary>A card whose run ended with failures: the same change for the failed devices as a new card that needs Run.</summary>
    [RelayCommand]
    private async Task RetryFailedAsync(AssistantPreviewItem? preview)
    {
        if (preview is not { CanRetryFailed: true } || ConversationId is not { } conversation)
        {
            return;
        }

        preview.RetriedFailed = true;
        try
        {
            var card = await _api.AssistantRetryFailedAsync(conversation, preview.Handle, preview.FailedDeviceIds, CancellationToken.None).ConfigureAwait(true);
            if (ConversationId == conversation)
            {
                Items.Add(new AssistantPreviewItem(card));
                JumpToLatest();
            }
        }
        catch (RpcException ex)
        {
            preview.RetriedFailed = false;
            ShowMessage(ex.Status.Detail, isError: true);
        }
    }

    // ---- Ctrl+J --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Ctrl+J: opens the panel and puts the focus into the message box; with the focus already in the panel it closes it.
    /// False: the assistant is not available (the key does nothing).
    /// </summary>
    public bool ToggleFromKeyboard(bool focusInPanel)
    {
        if (!IsAvailable)
        {
            return false;
        }

        if (IsOpen && focusInPanel)
        {
            Close();
            return true;
        }

        Open();
        FocusMessageRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
