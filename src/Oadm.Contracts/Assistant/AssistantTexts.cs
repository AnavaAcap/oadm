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
    public const string Ollama = "ollama";
    public const string OpenAiCompatible = "openai-compatible";

    /// <summary>Every provider id, in the order of the Settings card.</summary>
    public static IReadOnlyList<string> Providers { get; } = [Anthropic, OpenAi, Ollama, OpenAiCompatible];

    /// <summary>The model the panel suggests for Anthropic when the provider lists it.</summary>
    public const string AnthropicDefaultModel = "claude-opus-5-5";

    /// <summary>The name of a provider as the card and the notice show it ("Anthropic").</summary>
    public static string ProviderName(string? provider) => provider switch
    {
        Anthropic => "Anthropic",
        OpenAi => "OpenAI",
        Ollama => "Ollama",
        OpenAiCompatible => "OpenAI-compatible",
        _ => provider ?? string.Empty,
    };

    /// <summary>The provider runs outside the user's network (all but Ollama): the panel shows <see cref="Notice"/>.</summary>
    public static bool IsCloud(string? provider) => provider is Anthropic or OpenAi or OpenAiCompatible;

    /// <summary>The notice of a user's first message after the panel opened (cloud providers).</summary>
    public static string Notice(string? provider) => $"Your messages and the data the assistant reads go to {ProviderName(provider)}.";

    public const int MaxMessageLength = 8_000;
    public const int MaxSelectedDevices = 10_000;
    public const int MaxToolCallsPerMessage = 20;
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
    public const string DailyLimit = "The assistant reached today's limit. It works again tomorrow.";
    public const string PreviewEnded = "This preview has ended. Ask again.";
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

    /// <summary>The start suggestions of an empty panel.</summary>
    public static IReadOnlyList<string> Suggestions { get; } =
    [
        "Which devices are offline?",
        "Which devices have vulnerabilities?",
        "Which cameras run old AXIS OS?",
        "Show me a picture of a camera",
    ];
}
