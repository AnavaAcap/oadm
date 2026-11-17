namespace Oadm.Contracts.Assistant;

/// <summary>
/// The texts and limits of the assistant (docs/spec/assistant.md), shared by the server and the client's panel and Settings
/// card AI, section Assistant, so one concept has one phrase.
/// </summary>
public static class AssistantTexts
{
    /// <summary>The provider ids of <c>AssistantSettings.provider</c>.</summary>
    public const string Anthropic = "anthropic";
    public const string OpenAi = "openai";
    public const string Gemini = "gemini";
    public const string Ollama = "ollama";
    public const string OpenAiCompatible = "openai-compatible";

    /// <summary>Every provider id, in the order of the Settings card.</summary>
    public static IReadOnlyList<string> Providers { get; } = [Anthropic, OpenAi, Gemini, Ollama, OpenAiCompatible];

    /// <summary>The model the panel suggests for Anthropic when the provider lists it.</summary>
    public const string AnthropicDefaultModel = "claude-opus-5-5";

    /// <summary>The name of a provider as the card and the notice show it ("Anthropic").</summary>
    public static string ProviderName(string? provider) => provider switch
    {
        Anthropic => "Anthropic",
        OpenAi => "OpenAI",
        Gemini => "Google Gemini",
        Ollama => "Ollama",
        OpenAiCompatible => "OpenAI-compatible",
        _ => provider ?? string.Empty,
    };

    /// <summary>The provider runs outside the user's network (all but Ollama).</summary>
    public static bool IsCloud(string? provider) => provider is Anthropic or OpenAi or Gemini or OpenAiCompatible;

    public const int MaxMessageLength = 8_000;
    public const int MaxSelectedDevices = 10_000;
    public const int MaxToolCallsPerMessage = 20; // the default of Steps per answer
    public const int MaxPicturesToModel = 5;
    public const int MaxTableRows = 50;
    public const int MaxAttachments = 10;
    public const long DefaultDailyLimit = 2_000_000;

    // Status of GetSettings.
    public const string On = "On";
    public const string Off = "Off";

    // Refusals and errors (gRPC status details and AssistantDone.error).
    public const string IsOff = "The assistant is off.";
    public const string Busy = "Wait for the answer or stop it.";
    public const string NothingToRetry = "There is nothing to try again.";
    public const string DailyLimit = "The assistant reached today's limit. It works again tomorrow.";
    public const string PreviewEnded = "This preview has ended. Ask again.";

    /// <summary>A file of an answer that ended or is another user's (AssistantService.Download).</summary>
    public const string FileEnded = "This file has ended. Ask again.";

    // Looking in the Axis docs (docs/spec/assistant.md "Looking on axis.com").

    /// <summary>The question of the offer's card.</summary>
    public const string DocsOfferQuestion = "OADM has no function for this. Should I look it up on axis.com?";

    /// <summary>The card's button that says yes.</summary>
    public const string LookUpOnline = "Look up online";

    /// <summary>The card's button that says no (nothing is sent).</summary>
    public const string NotNow = "Not now";

    /// <summary>The user's message the click sends with the offer's id.</summary>
    public const string LookUpMessage = "Yes, look it up on axis.com.";

    /// <summary>The card after the click, and after Not now.</summary>
    public const string LookedUpOnline = "Looked up online.";
    public const string NotLookedUp = "Not looked up.";

    /// <summary>Chat with an offer that is unknown, used, ended or another chat's.</summary>
    public const string DocsOfferEnded = "This offer has ended. Ask again.";

    /// <summary>The only site a lookup may search and read: axis.com with its subdomains (help, developer, www).</summary>
    public static IReadOnlyList<string> DocsDomains { get; } = ["axis.com"];

    /// <summary>The one line of a provider that cannot look online (Google Gemini, Ollama, OpenAI-compatible).</summary>
    public static string CannotLookOnline(string? provider) => $"{ProviderName(provider)} cannot look online, so axis.com cannot be read here.";

    /// <summary>The end of a lookup answer whose provider cannot be reached at all (the server has no way out).</summary>
    public const string DocsNotReached = "axis.com could not be read. The server may have no internet.";

    /// <summary>
    /// A plain tool line: the provider reports one search or read on axis.com as failed (its own servers read the pages, so
    /// this never means the server has no internet; the answer says what it could not read).
    /// </summary>
    public const string DocsPageNotRead = "A page on axis.com could not be read.";

    /// <summary>A tool line while the provider searches or reads the Axis docs.</summary>
    public const string LookingInDocs = "Looking on axis.com";

    /// <summary>Secrets of one Run at most (AssistantRunRequest.secrets).</summary>
    public const int MaxRunSecrets = 8;

    /// <summary>The label of a secret field: "password" is "Password".</summary>
    public static string SecretLabel(string label) => string.IsNullOrEmpty(label) ? label : char.ToUpperInvariant(label[0]) + label[1..];

    /// <summary>The error below an empty secret field: "Enter the password." (the server's refusal of Run says the same).</summary>
    public static string EnterSecret(string label) => "Enter the " + label + ".";
    public const string NoAnswer = "The provider did not answer. Try again.";
    public const string TooManySelected = "Select at most 10,000 devices for the assistant.";
    public const string EmptyMessage = "Write a message.";
    public const string MessageTooLong = "Write at most 8,000 characters.";
    public const string FileGone = "This file is no longer there. Attach it again.";
    public const string TooManyFiles = "Attach at most 10 files.";
    public const string ToolLimit = "The assistant used 20 tools for this message. Ask again in smaller steps.";
    /// <summary>The done error of an answer whose chosen model refused the tools.</summary>
    public const string CannotUseTools = "This model cannot use tools. Choose another one.";

    /// <summary>Test: the provider read the model list (address and key work).</summary>
    public const string ProviderAnswered = "The provider answered.";

    /// <summary>A tool line: the assistant reached its limit of network scans and other outside calls a minute in this session.</summary>
    public const string TooManyChanges = "The assistant did this too often. Wait a minute.";

    /// <summary>A tool line: the user's camera pictures of this minute are used up (the same limit as everywhere).</summary>
    public const string TooManyPictures = "Too many pictures. Wait a minute.";

    /// <summary>The done error when OADM itself failed (the server log says why); never a provider's error.</summary>
    public const string Failed = "The assistant ran into a problem. The server log says why.";

    /// <summary>Chat without a model (trailer oadm-field "model").</summary>
    public const string ChooseModel = "Choose a model.";

    // Field errors of SetSettings (trailer oadm-field).
    public const string ChooseProvider = "Choose a provider.";
    public const string EnterAddress = "Enter the address, e.g. http://10.0.0.5:11434.";
    public const string AddressNotValid = "Enter an address that starts with http:// or https://.";
    public const string ModelTooLong = "Use at most 200 characters.";
    public const string EnterApiKey = "Enter the API key.";
    public const string ApiKeyTooLong = "Use at most 500 characters.";
    public const string DailyLimitRange = "Enter 1,000 to 1,000,000,000 tokens.";
    public const string UserDailyLimitRange = "Enter 0 for no limit, or 1,000 to 1,000,000,000 tokens.";

    /// <summary>The trailer of the field a SetSettings refusal names.</summary>
    public const string FieldTrailer = "oadm-field";

    public const long MinDailyLimit = 1_000;
    public const long MaxDailyLimit = 1_000_000_000;

    /// <summary>Steps per answer (the model's tool calls of one message), set on the card; 0 from an older client: the default.</summary>
    public const int MinStepsPerAnswer = 1;
    public const int MaxStepsPerAnswer = 50;
    public const string StepsRange = "Enter 1 to 50.";

    /// <summary>The longest one answer may take, in seconds, set on the card; 0 from an older client: the default.</summary>
    public const int DefaultAnswerSeconds = 120;
    public const int MinAnswerSeconds = 30;
    public const int MaxAnswerSeconds = 600;
    public const string AnswerSecondsRange = "Enter 30 to 600 seconds.";

    /// <summary>The start suggestions of an empty panel.</summary>
    public static IReadOnlyList<string> Suggestions { get; } =
    [
        "Which devices are offline?",
        "Which devices have vulnerabilities?",
        "Which cameras run old AXIS OS?",
        "Show me a picture of a camera",
    ];
}
