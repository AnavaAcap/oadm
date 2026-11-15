using System.Globalization;

using Shared = Oadm.Contracts.Assistant.AssistantTexts;

namespace Oadm.Client.Assistant;

/// <summary>
/// The texts of the Assistant panel and its settings (docs/spec/assistant.md), one phrase per concept: those the server
/// says too come from <see cref="Shared"/> (Oadm.Contracts), the client's own are here.
/// </summary>
public static class AssistantTexts
{
    public const string Title = "Assistant";
    public const string Clear = "Clear";
    public const string Close = "Close";
    public const string Send = "Send";
    public const string Stop = "Stop";
    public const string AttachFile = "Attach file";
    public const string Copy = "Copy";
    public const string Copied = "Copied.";
    public const string Run = "Run";
    public const string Cancel = "Cancel";
    public const string ShowTasks = "Show tasks";
    public const string SelectAllInGrid = "Select all in the grid";
    public const string Placeholder = "Ask about your devices";
    public const string Stopped = "Stopped.";
    public const string Cancelled = "Cancelled.";
    public const string Starting = "Starting the tasks";
    public const string Uploading = "Uploading the file";
    public const string RemoveFile = "Remove file";
    public const string Enlarge = "Show larger";
    public const string Thinking = "Thinking";
    public const string TryAgain = "Try again";
    public const string Edit = "Edit";
    public const string GoToLatest = "Go to the latest";
    public const string DropFiles = "Drop files to attach";
    public const string NothingToRetry = Shared.NothingToRetry;
    public const string ChooseDevices = "Choose devices";
    public const string RemoveDevices = "Remove devices";
    public const string Select = "Select";
    public const string OpenLiveView = "Open live view";
    public const string OpenWebInterface = "Open web interface";
    public const string RetryFailed = "Try the failed devices again";
    public const string FailedReason = "Failed";
    public const string CopyChat = "Copy chat";
    public const string CopyCsv = Oadm.Client.Controls.MarkdownText.CopyCsv;
    public const string SaveCsv = Oadm.Client.Controls.MarkdownText.SaveCsv;
    public const string StartHeading = "What can I do for your devices?";

    /// <summary>The title bar button's tooltip with its key.</summary>
    public const string TitleTip = "Assistant (Ctrl+J)";

    // The empty panel's chips (live counts) and what a click asks.
    public const string AskSummary = "Give me a summary of my devices.";
    public const string AskOffline = "Which devices are offline?";
    public const string AskVulnerable = "Which devices have vulnerabilities?";

    public static string DevicesChip(int count) => count == 1 ? "1 device" : string.Create(CultureInfo.CurrentCulture, $"{count:N0} devices");

    public static string OfflineChip(int count) => string.Create(CultureInfo.CurrentCulture, $"{count:N0} offline");

    public static string VulnerableChip(int count) => string.Create(CultureInfo.CurrentCulture, $"{count:N0} with vulnerabilities");

    // The empty panel's cards: the selection, the page, then the general ones.
    public static string RestartSelected(int count) =>
        count == 1 ? "Restart the selected device" : string.Create(CultureInfo.CurrentCulture, $"Restart the {count:N0} selected devices");

    public const string PictureOfSelected = "Show a picture of the selected camera";
    public const string FirmwareOfSelected = "Which AXIS OS do the selected devices run?";
    public const string OldFirmware = "Which cameras run old AXIS OS?";
    public const string PictureOfACamera = "Show me a picture of a camera";
    public const string ExpiringCertificates = "Which device certificates expire soon?";
    public const string HttpsOff = "Which devices do not use HTTPS?";
    public const string RulesOn = "Which event rules are on?";
    public const string RulesFailing = "Which event rules failed today?";
    public const string ComplianceFailing = "Which devices fail compliance?";
    public const string SitesOffline = "Which sites are offline?";
    public const string LogProblems = "What went wrong on the server today?";
    public const string RecentTasks = "Which tasks failed today?";

    /// <summary>The devices chosen with @: "3 devices", or the device's name when one.</summary>
    public static string ChosenDevices(int count, string? oneName) => count == 1 && !string.IsNullOrEmpty(oneName) ? oneName! : Devices(count);

    /// <summary>The end of a run with failures: "11 done, 1 failed: Wrong password".</summary>
    public static string RunResult(int done, int failed, int cancelled, IReadOnlyList<(string Reason, int Count)> reasons)
    {
        var parts = new List<string> { string.Create(CultureInfo.CurrentCulture, $"{done:N0} done") };
        if (cancelled > 0)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"{cancelled:N0} cancelled"));
        }

        var failedText = string.Create(CultureInfo.CurrentCulture, $"{failed:N0} failed");
        if (reasons.Count > 0)
        {
            var shown = reasons.Take(3).Select(r => reasons.Count == 1 ? r.Reason : string.Create(CultureInfo.CurrentCulture, $"{r.Reason} ({r.Count:N0})")).ToList();
            if (reasons.Count > 3)
            {
                shown.Add(More(reasons.Count - 3));
            }

            failedText += ": " + string.Join(", ", shown);
        }

        parts.Add(failedText);
        return string.Join(", ", parts);
    }

    /// <summary>The name of a picture pasted from the clipboard: "Pasted image 2.png".</summary>
    public static string PastedImage(int number) => string.Create(CultureInfo.InvariantCulture, $"Pasted image {number}.png");

    // The columns of a device table in the chat.
    public const string ColumnName = "Name";
    public const string ColumnAddress = "Address";
    public const string ColumnModel = "Model";
    public const string ColumnStatus = "Status";

    /// <summary>A skipped line of a preview card: "Skipped: 3 devices: Site offline".</summary>
    public static string Skipped(string reason) => "Skipped: " + reason;

    /// <summary>The four start suggestions of an empty panel; a click sends one.</summary>
    public static IReadOnlyList<string> Suggestions => Shared.Suggestions;

    /// <summary>The most devices of the grid's selection sent with a message.</summary>
    public const int MaxSelection = Shared.MaxSelectedDevices;

    /// <summary>The longest message.</summary>
    public const int MaxMessageLength = Shared.MaxMessageLength;

    /// <summary>The most files attached to one message.</summary>
    public const int MaxAttachments = Shared.MaxAttachments;

    public const string TooManySelected = Shared.TooManySelected;
    public const string TooManyFiles = Shared.TooManyFiles;
    public const string EnterMessage = Shared.EmptyMessage;
    public const string WaitForAnswer = Shared.Busy;
    public const string WaitForUpload = "Wait for the file to upload.";

    /// <summary>"12 devices selected" below the message box; none: nothing.</summary>
    public static string SelectionCount(int count) => count switch
    {
        0 => string.Empty,
        1 => "1 device selected",
        _ => string.Create(CultureInfo.CurrentCulture, $"{count:N0} devices selected"),
    };

    /// <summary>The count of a preview card: "12 devices".</summary>
    public static string Devices(int count) =>
        count == 1 ? "1 device" : string.Create(CultureInfo.CurrentCulture, $"{count:N0} devices");

    /// <summary>"+38 more" below a table or a card's device list.</summary>
    public static string More(int count) => string.Create(CultureInfo.CurrentCulture, $"+{count:N0} more");

    /// <summary>The state of a preview's tasks after Run.</summary>
    public static string RunState(int total, int done, int failed, int cancelled, int active)
    {
        if (active > 0)
        {
            return string.Create(CultureInfo.CurrentCulture, $"Running: {done + failed + cancelled:N0} of {total:N0} ended");
        }

        var parts = new List<string>();
        if (done > 0)
        {
            parts.Add("done on " + Devices(done));
        }

        if (failed > 0)
        {
            parts.Add("failed on " + Devices(failed));
        }

        if (cancelled > 0)
        {
            parts.Add("cancelled on " + Devices(cancelled));
        }

        var text = parts.Count == 0 ? "done" : string.Join(", ", parts);
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    // The card Assistant of the page MCP & Assistant.
    public const string Saved = "Saved";
    public const string ApiKeyHint = "Leave empty to keep the saved key.";
    public const string OllamaPrivacy = "Ollama keeps everything in your network.";
    public const string NoProvider = Shared.ChooseProvider;
    public const string ChooseModel = Shared.ChooseModel;
    public const string ModelTooLong = Shared.ModelTooLong;
    public const string EnterAddress = Shared.EnterAddress;
    public const string AddressNotValid = Shared.AddressNotValid;
    public const string EnterApiKey = Shared.EnterApiKey;
    public const string ApiKeyTooLong = Shared.ApiKeyTooLong;
    public const string LimitRange = Shared.DailyLimitRange;
    public const string UserLimitRange = Shared.UserDailyLimitRange;
    public const string NoChanges = "No changes to save.";
    public const string Testing = "Testing the provider";
    public const string ModelTip = "Model";
    public const string LoadingModels = "Reading the models";

    /// <summary>The note under the provider for a cloud one.</summary>
    public static string CloudPrivacy(string provider) =>
        $"Device names, addresses and other data of your devices go to {provider}.";

    /// <summary>"18,420 tokens".</summary>
    public static string Tokens(long count) => string.Create(CultureInfo.CurrentCulture, $"{count:N0} tokens");

    /// <summary>"95,333 tokens today": the server's use of today in the panel's composer, next to Send.</summary>
    public static string TokensToday(long count) => Tokens(count) + " today";
}
