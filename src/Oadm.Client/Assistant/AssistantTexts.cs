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
    public const string NewChat = "New chat";
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

    // Settings, section Assistant (Settings card AI).
    public const string SectionIntro = "Chat with an AI model in OADM. It works with the rights of the signed-in user.";
    public const string Saved = "Saved";
    public const string ApiKeyHint = "Leave empty to keep the saved key.";
    public const string PicturesHint = "Camera pictures always show in the chat.";
    public const string OllamaPrivacy = "Ollama keeps everything in your network.";
    public const string NoProvider = Shared.ChooseProvider;
    public const string EnterModel = Shared.EnterModel;
    public const string ModelTooLong = Shared.ModelTooLong;
    public const string EnterAddress = Shared.EnterAddress;
    public const string AddressNotValid = Shared.AddressNotValid;
    public const string EnterApiKey = Shared.EnterApiKey;
    public const string ApiKeyTooLong = Shared.ApiKeyTooLong;
    public const string LimitRange = Shared.DailyLimitRange;
    public const string UserLimitRange = Shared.UserDailyLimitRange;
    public const string NoChanges = "No changes to save.";
    public const string Testing = "Testing the provider";
    public const string ModelSuggestion = Shared.AnthropicDefaultModel;

    /// <summary>The note under the provider for a cloud one.</summary>
    public static string CloudPrivacy(string provider) =>
        $"Device names, addresses and other data of your devices go to {provider}.";

    /// <summary>"18,420 tokens" (Used today).</summary>
    public static string Tokens(long count) => string.Create(CultureInfo.CurrentCulture, $"{count:N0} tokens");
}
