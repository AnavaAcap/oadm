using System.Globalization;
using System.Text;

using Oadm.Contracts.Assistant;
using Oadm.Core.Security;
using Oadm.Core.Settings;

namespace Oadm.Server.Assistant;

/// <summary>
/// The stored settings of the assistant (setting <see cref="Key"/>; docs/spec/assistant.md "Settings"): off by default,
/// changed only by administrators on the Settings card (audited as "Changed assistant"). The API key is stored encrypted with
/// the master key (<see cref="ProtectedApiKey"/>), never logged and never sent to a client.
/// </summary>
public sealed record AssistantSettings
{
    /// <summary>The key of the setting.</summary>
    public const string Key = "Assistant";

    /// <summary>The associated data the API key is encrypted with (it never decrypts as another secret).</summary>
    private static readonly byte[] KeyPurpose = Encoding.UTF8.GetBytes("oadm-assistant:api-key");

    public bool Enabled { get; init; }

    public string Provider { get; init; } = AssistantTexts.Anthropic;

    /// <summary>The address of Ollama and of an OpenAI-compatible provider; null for the others.</summary>
    public string? Address { get; init; }

    public string Model { get; init; } = string.Empty;

    /// <summary>The API key, encrypted (base64 of AES-256-GCM with the master key); null without one.</summary>
    public string? ProtectedApiKey { get; init; }

    public bool SendPictures { get; init; }

    public long DailyLimit { get; init; } = AssistantTexts.DefaultDailyLimit;

    /// <summary>Tokens a day per user; 0: no limit per user.</summary>
    public long UserDailyLimit { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasApiKey => !string.IsNullOrEmpty(ProtectedApiKey);

    /// <summary>The stored settings; the defaults (off) when none are stored or the stored value cannot be read.</summary>
    public static async Task<AssistantSettings> LoadAsync(ServerSettingsStore store, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        try
        {
            return await store.GetAsync<AssistantSettings>(Key, ct).ConfigureAwait(false) ?? new AssistantSettings();
        }
        catch (System.Text.Json.JsonException)
        {
            return new AssistantSettings(); // fail closed: off
        }
    }

    public Task SaveAsync(ServerSettingsStore store, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.SetAsync(Key, this, ct);
    }

    /// <summary>The key encrypted for <see cref="ProtectedApiKey"/>.</summary>
    public static string Protect(CredentialProtector protector, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(protector);
        return Convert.ToBase64String(protector.Protect(apiKey, KeyPurpose));
    }

    /// <summary>The API key in plain text, for one call to the provider; null without one or when it cannot be read.</summary>
    public string? ApiKey(CredentialProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);
        if (!HasApiKey)
        {
            return null;
        }

        try
        {
            return protector.Unprotect(Convert.FromBase64String(ProtectedApiKey!), KeyPurpose);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
        {
            return null; // another master key (never logged): the administrator enters the key again
        }
    }

    /// <summary>The provider needs an address (Ollama, OpenAI-compatible).</summary>
    public static bool NeedsAddress(string provider) => provider is AssistantTexts.Ollama or AssistantTexts.OpenAiCompatible;

    /// <summary>The provider needs an API key (all but Ollama).</summary>
    public static bool NeedsApiKey(string provider) => provider is not AssistantTexts.Ollama;

    /// <summary>"On", "Off" or "Choose a model." for the Settings card.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Status => !Enabled ? AssistantTexts.Off : string.IsNullOrWhiteSpace(Model) ? AssistantTexts.ChooseModel : AssistantTexts.On;

    /// <summary>"Turned on, model a -> b, API key changed" for the audit log (never the key).</summary>
    public static string Changes(AssistantSettings before, AssistantSettings after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var changes = new List<string>();
        if (before.Enabled != after.Enabled)
        {
            changes.Add(after.Enabled ? "Turned on" : "Turned off");
        }

        if (before.Provider != after.Provider)
        {
            changes.Add($"Provider {AssistantTexts.ProviderName(before.Provider)} -> {AssistantTexts.ProviderName(after.Provider)}");
        }

        if (!string.Equals(before.Address ?? string.Empty, after.Address ?? string.Empty, StringComparison.Ordinal))
        {
            changes.Add($"Address {Or(before.Address)} -> {Or(after.Address)}");
        }

        if (!string.Equals(before.Model, after.Model, StringComparison.Ordinal))
        {
            changes.Add($"Model {Or(before.Model)} -> {Or(after.Model)}");
        }

        if (!string.Equals(before.ProtectedApiKey, after.ProtectedApiKey, StringComparison.Ordinal))
        {
            changes.Add(after.HasApiKey ? "API key changed" : "API key removed");
        }

        if (before.SendPictures != after.SendPictures)
        {
            changes.Add(after.SendPictures ? "Send pictures to the model on" : "Send pictures to the model off");
        }

        if (before.DailyLimit != after.DailyLimit)
        {
            changes.Add(string.Create(CultureInfo.InvariantCulture, $"Daily limit {before.DailyLimit:N0} -> {after.DailyLimit:N0}"));
        }

        if (before.UserDailyLimit != after.UserDailyLimit)
        {
            changes.Add($"Daily limit per user {PerUser(before.UserDailyLimit)} -> {PerUser(after.UserDailyLimit)}");
        }

        return changes.Count == 0 ? "No change" : string.Join(", ", changes);
    }

    private static string Or(string? value) => string.IsNullOrEmpty(value) ? "none" : value;

    private static string PerUser(long limit) => limit == 0 ? "none" : limit.ToString("N0", CultureInfo.InvariantCulture);
}
