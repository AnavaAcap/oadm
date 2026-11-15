using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using Oadm.Core.Security;

using static Oadm.Server.Tools.ToolSchema;

namespace Oadm.Server.Tools;

/// <summary>A secret a preview needs for its run (a password, a key): its name in the answer and what the person is asked for.</summary>
/// <param name="Name">Lower case words with underscores, e.g. <c>password</c>; the run reads it with <see cref="ToolCall.Secret"/>.</param>
/// <param name="Label">What the field is called, lower case, e.g. "password", "PKCS#12 password"; the refusal reads "Enter the &lt;label&gt;.".</param>
/// <param name="MaxLength">The longest value in characters (at most <see cref="ToolSecrets.MaxBytes"/> bytes in UTF-8 anyway).</param>
internal sealed record ToolSecretSlot(string Name, string Label, int MaxLength = 1024);

/// <summary>
/// The secret channel (docs/spec/mcp.md "Secrets"): a secret reaches a run without ever passing through a model. A preview
/// declares slots for its handle (<see cref="Declare"/>); the person fills them, over MCP through a one-time link the agent's
/// shell sends to (<c>PUT /secrets/&lt;token&gt;</c>, <see cref="SetByLink"/>), in the assistant in the preview card's field
/// (sent with Run, <see cref="Set"/>); the registry refuses the run while one is missing ("Enter the password.") and hands the
/// values to the run tool once (<see cref="Take"/>). Values are held in server memory only, encrypted with the master key and
/// bound to the caller's binding key, the handle and the slot (AES-GCM associated data); they end with the handle
/// (<see cref="ToolHandles.Lifetime"/>), are never logged, never written to disk and never part of an answer.
/// </summary>
internal sealed class ToolSecrets(TimeProvider time, CredentialProtector protector)
{
    /// <summary>The largest value (UTF-8 bytes), also the largest body of <c>PUT /secrets/</c>.</summary>
    public const int MaxBytes = 4096;

    /// <summary>Slots of one preview at most.</summary>
    public const int MaxSlots = 8;

    /// <summary>Previews with open slots per caller at most (the oldest go first).</summary>
    public const int MaxPerCaller = 20;

    public const string LinkEnded = "This secret link has ended or was used. Call the preview again.";
    public const string NoSuchSlot = "This preview needs no such secret.";
    public const string TooLong = "The value is too long.";
    public const string Empty = "Send the value in the body.";

    /// <summary>"Enter the password." for a slot that is still missing.</summary>
    public static string Enter(ToolSecretSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        return "Enter the " + slot.Label + ".";
    }

    /// <summary>When slots declared now end.</summary>
    public DateTimeOffset EndOfNew() => time.GetUtcNow() + ToolHandles.Lifetime;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _byHandle = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Handle, string Slot)> _links = new(StringComparer.Ordinal);
    private long _seq;

    /// <summary>
    /// Declares the slots of a preview's handle for its run tool <paramref name="runTool"/>; answers one link token per slot
    /// (once each). A handle declared again gets its slots anew.
    /// </summary>
    public IReadOnlyDictionary<string, string> Declare(string bindingKey, string handle, string runTool, IReadOnlyList<ToolSecretSlot> slots)
    {
        ArgumentException.ThrowIfNullOrEmpty(bindingKey);
        ArgumentException.ThrowIfNullOrEmpty(handle);
        ArgumentNullException.ThrowIfNull(slots);
        if (slots.Count is 0 or > MaxSlots || slots.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count() != slots.Count)
        {
            throw new ArgumentException("A preview declares 1 to 8 secrets with different names.", nameof(slots));
        }

        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        lock (_gate)
        {
            DropEnded();
            Remove(handle);
            var mine = _byHandle.Values.Where(e => e.BindingKey == bindingKey).OrderBy(e => e.Seq).ToList();
            foreach (var oldest in mine.Take(Math.Max(0, mine.Count - MaxPerCaller + 1)))
            {
                Remove(oldest.Handle);
            }

            var entry = new Entry(bindingKey, handle, runTool, [.. slots], time.GetUtcNow() + ToolHandles.Lifetime, ++_seq);
            foreach (var slot in slots)
            {
                var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                _links[token] = (handle, slot.Name);
                entry.Tokens.Add(token);
                tokens[slot.Name] = token;
            }

            _byHandle[handle] = entry;
        }

        return tokens;
    }

    /// <summary>The slots of <paramref name="handle"/> for <paramref name="bindingKey"/>; empty when it has none (or is another caller's).</summary>
    public IReadOnlyList<ToolSecretSlot> SlotsOf(string bindingKey, string? handle)
    {
        lock (_gate)
        {
            DropEnded();
            return Find(bindingKey, handle)?.Slots ?? [];
        }
    }

    /// <summary>
    /// A value sent to a link (MCP): the link once, only for its caller; answers the slot and its handle's run tool, or the
    /// refusal. Another caller's link answers like an ended one and stays theirs.
    /// </summary>
    public (ToolSecretSlot? Slot, string? RunTool, string? Error) SetByLink(string bindingKey, string? token, string value)
    {
        lock (_gate)
        {
            DropEnded();
            if (string.IsNullOrEmpty(token) || !_links.TryGetValue(token, out var link) || Find(bindingKey, link.Handle) is not { } entry)
            {
                return (null, null, LinkEnded);
            }

            var slot = entry.Slots.First(s => s.Name == link.Slot);
            if (Check(slot, value) is { } error)
            {
                return (slot, entry.RunTool, error); // the link stays for a corrected value
            }

            _links.Remove(token);
            entry.Tokens.Remove(token);
            entry.Values[slot.Name] = Protect(entry, slot.Name, value);
            return (slot, entry.RunTool, null);
        }
    }

    /// <summary>A value the person typed into the assistant's card (sent with Run): null when it was taken, else the refusal.</summary>
    public string? Set(string bindingKey, string? handle, string name, string value)
    {
        lock (_gate)
        {
            DropEnded();
            if (Find(bindingKey, handle) is not { } entry || entry.Slots.FirstOrDefault(s => s.Name == name) is not { } slot)
            {
                return NoSuchSlot;
            }

            if (Check(slot, value) is { } error)
            {
                return error;
            }

            entry.Values[name] = Protect(entry, name, value);
            return null;
        }
    }

    /// <summary>The slots of <paramref name="handle"/> that have no value yet (for the run tool <paramref name="runTool"/>).</summary>
    public IReadOnlyList<ToolSecretSlot> Missing(string bindingKey, string? handle, string runTool)
    {
        lock (_gate)
        {
            DropEnded();
            return Find(bindingKey, handle) is { } entry && entry.RunTool == runTool ? [.. entry.Slots.Where(s => !entry.Values.ContainsKey(s.Name))] : [];
        }
    }

    /// <summary>
    /// The values of <paramref name="handle"/> for its run tool, taken (a run reads them once): null when the handle declared
    /// none, is another caller's or another run tool's.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Take(string bindingKey, string? handle, string runTool)
    {
        lock (_gate)
        {
            DropEnded();
            if (Find(bindingKey, handle) is not { } entry || entry.RunTool != runTool)
            {
                return null;
            }

            Remove(entry.Handle);
            return entry.Values.ToDictionary(v => v.Key, v => protector.Unprotect(v.Value, Associated(entry, v.Key)), StringComparer.Ordinal);
        }
    }

    private static string? Check(ToolSecretSlot slot, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return Enter(slot);
        }

        return value.Length > slot.MaxLength || Encoding.UTF8.GetByteCount(value) > MaxBytes || value.Contains('\0', StringComparison.Ordinal) ? TooLong : null;
    }

    private byte[] Protect(Entry entry, string slot, string value) => protector.Protect(value, Associated(entry, slot));

    private static byte[] Associated(Entry entry, string slot) => Encoding.UTF8.GetBytes(entry.BindingKey + "\n" + entry.Handle + "\n" + slot);

    private Entry? Find(string bindingKey, string? handle) =>
        handle is not null && _byHandle.TryGetValue(handle, out var entry) && string.Equals(entry.BindingKey, bindingKey, StringComparison.Ordinal) ? entry : null;

    private void Remove(string handle)
    {
        if (_byHandle.Remove(handle, out var entry))
        {
            foreach (var token in entry.Tokens)
            {
                _links.Remove(token);
            }

            foreach (var value in entry.Values.Values)
            {
                CryptographicOperations.ZeroMemory(value);
            }
        }
    }

    private void DropEnded()
    {
        var now = time.GetUtcNow();
        foreach (var ended in _byHandle.Values.Where(e => e.Ends <= now).Select(e => e.Handle).ToList())
        {
            Remove(ended);
        }
    }

    private sealed record Entry(string BindingKey, string Handle, string RunTool, IReadOnlyList<ToolSecretSlot> Slots, DateTimeOffset Ends, long Seq)
    {
        public Dictionary<string, byte[]> Values { get; } = new(StringComparer.Ordinal);

        public List<string> Tokens { get; } = [];
    }
}

/// <summary>
/// What a preview with secret slots adds to its answer (docs/spec/mcp.md "Secrets"): <c>needs_secrets</c> (the names) and,
/// on a channel that takes them (MCP), <c>secret_links</c> with the one-time link and the shell commands per name. The guide for
/// a tool: declare the slots after the handle is made, list <see cref="OutputProperties"/> in the preview's output schema,
/// read the values in the run with <see cref="ToolCall.Secret"/>; never echo one in an answer, a log line or an audit entry.
/// </summary>
internal static class ToolSecretAnswers
{
    /// <summary>The output schema entries of a preview that may need secrets.</summary>
    public static IEnumerable<KeyValuePair<string, JsonObject>> OutputProperties() =>
    [
        P("needs_secrets", OutList("Secrets the run needs, by name. Never ask for them in the chat: the user enters them through secret_links (or in OADM's card).", Out("string", "A secret's name."))),
        P("secret_links", OrNull(new JsonObject
        {
            ["type"] = "object",
            ["description"] = "Per secret: a one-time link (10 minutes) and the command the user runs in their own terminal. The value never passes the model.",
            ["additionalProperties"] = OutObject(null,
            [
                P("url", Out("string", "PUT the value here with your access key (once).")),
                P("command", Out("string", "For a POSIX shell (bash, zsh): asks for the value without showing it and sends it.")),
                P("command_powershell", Out("string", "For PowerShell 7.")),
                P("expires", Out("string", "When the link ends (UTC).")),
            ], "url", "command", "command_powershell", "expires"),
        })),
    ];

    /// <summary>
    /// Declares <paramref name="slots"/> for the preview's <paramref name="handle"/> and its run tool, and adds
    /// <c>needs_secrets</c> and <c>secret_links</c> to <paramref name="answer"/>.
    /// </summary>
    public static void Declare(ToolCall call, JsonObject answer, string handle, string runTool, IReadOnlyList<ToolSecretSlot> slots)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(answer);
        var secrets = call.SecretStore ?? throw new InvalidOperationException("This registry has no secret channel.");
        var tokens = secrets.Declare(call.Caller.BindingKey, handle, runTool, slots);
        answer["needs_secrets"] = new JsonArray([.. slots.Select(s => (JsonNode)JsonValue.Create(s.Name)!)]);
        if (call.Caller.Channel.SecretsUrl is not { } baseUrl)
        {
            answer["secret_links"] = null;
            return;
        }

        var links = new JsonObject();
        var expires = ToolFiles.Time(secrets.EndOfNew());
        foreach (var slot in slots)
        {
            var url = baseUrl + tokens[slot.Name];
            var prompt = char.ToUpperInvariant(slot.Label[0]) + slot.Label[1..];
            links[slot.Name] = new JsonObject
            {
                ["url"] = url,
                ["command"] = $"printf '{prompt}: ' >&2; read -rs OADM_SECRET; echo >&2; printf %s \"$OADM_SECRET\" | curl --cacert oadm-ca.pem -H \"Authorization: Bearer $OADM_KEY\" -X PUT --data-binary @- {url}; unset OADM_SECRET",
                ["command_powershell"] = $"$s = Read-Host '{prompt}' -MaskInput; Invoke-RestMethod -Method Put -Uri '{url}' -Headers @{{ Authorization = \"Bearer $env:OADM_KEY\" }} -ContentType 'text/plain; charset=utf-8' -Body $s; Remove-Variable s",
                ["expires"] = expires,
            };
        }

        answer["secret_links"] = links;
    }
}
