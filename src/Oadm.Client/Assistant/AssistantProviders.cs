namespace Oadm.Client.Assistant;

/// <summary>A provider of the Assistant's model (docs/spec/assistant.md "Settings").</summary>
/// <param name="Id">The contract's value ("anthropic").</param>
/// <param name="Name">The name the user sees ("Anthropic").</param>
/// <param name="NeedsKey">An API key is needed (every one but Ollama).</param>
/// <param name="NeedsAddress">An address is needed (Ollama, OpenAI-compatible).</param>
/// <param name="IsCloud">The data leaves the network (the privacy note).</param>
public sealed record AssistantProvider(string Id, string Name, bool NeedsKey, bool NeedsAddress, bool IsCloud)
{
    public static readonly AssistantProvider Anthropic = new("anthropic", "Anthropic", NeedsKey: true, NeedsAddress: false, IsCloud: true);
    public static readonly AssistantProvider OpenAi = new("openai", "OpenAI", NeedsKey: true, NeedsAddress: false, IsCloud: true);
    public static readonly AssistantProvider Ollama = new("ollama", "Ollama", NeedsKey: false, NeedsAddress: true, IsCloud: false);
    public static readonly AssistantProvider OpenAiCompatible = new("openai-compatible", "OpenAI-compatible", NeedsKey: true, NeedsAddress: true, IsCloud: true);

    public static readonly IReadOnlyList<AssistantProvider> All = [Anthropic, OpenAi, Ollama, OpenAiCompatible];

    public static AssistantProvider? Find(string? id) => All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));

    public override string ToString() => Name;
}

/// <summary>
/// Whether the Assistant is on, as the client knows it: read with <c>AssistantService.GetSettings</c> on every connect, set
/// again after the Settings card saved. The title bar button shows while it is on and the role has the permission.
/// </summary>
public sealed class AssistantAvailability
{
    /// <summary>The server has the assistant and it is on.</summary>
    public bool IsOn { get; private set; }

    /// <summary>The provider's name ("Anthropic"), empty while unknown.</summary>
    public string ProviderName { get; private set; } = string.Empty;

    /// <summary>Raised after <see cref="IsOn"/> or the provider changed.</summary>
    public event EventHandler? Changed;

    public void Apply(Contracts.V1.AssistantSettingsState? state)
    {
        bool on = state?.Settings is { Enabled: true };
        string provider = AssistantProvider.Find(state?.Settings?.Provider)?.Name ?? string.Empty;
        if (on != IsOn || provider != ProviderName)
        {
            IsOn = on;
            ProviderName = provider;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
