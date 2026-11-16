using Microsoft.Extensions.AI;

using Oadm.Core.Auth;

namespace Oadm.Server.Assistant;

/// <summary>
/// One open panel's chat (docs/spec/assistant.md "Where it runs"): its messages, the previews it showed (handle and run tool),
/// the answer that streams now and the notes for the model's next message. Server memory only.
/// </summary>
internal sealed class AssistantConversation(string sessionKey, Guid userId, string id, DateTimeOffset now)
{
    public string SessionKey { get; } = sessionKey;

    public Guid UserId { get; } = userId;

    public string Id { get; } = id;

    public Lock Gate { get; } = new();

    /// <summary>The turns so far, oldest first: each the user's message and what followed (assistant, tool results).</summary>
    public List<List<ChatMessage>> Turns { get; } = [];

    /// <summary>The previews shown in this chat that may still run: handle to its tools and input.</summary>
    public Dictionary<string, AssistantPreview> Previews { get; } = new(StringComparer.Ordinal);

    /// <summary>The previews of this chat that ran (Try the failed devices again), the newest <see cref="MaxRuns"/>.</summary>
    public Dictionary<string, AssistantPreview> Runs { get; } = new(StringComparer.Ordinal);

    public const int MaxRuns = 50;

    /// <summary>What happened outside the model (a Run) for its next message, as data.</summary>
    public List<string> Notes { get; } = [];

    /// <summary>
    /// The offers of the last answer to look it up on axis.com (docs/spec/assistant.md "Looking on axis.com"): id to
    /// its end. The next message takes one (Look up online) or drops them all.
    /// </summary>
    public Dictionary<string, AssistantDocsOffer> DocsOffers { get; } = new(StringComparer.Ordinal);

    public DateTimeOffset LastUsed { get; set; } = now;

    /// <summary>The answer that streams now; null between answers.</summary>
    public CancellationTokenSource? Streaming { get; set; }

    /// <summary>Stop was pressed (<see cref="StopAt"/>): it stops the answer that streams, or the next one that begins soon after.</summary>
    public bool StopRequested { get; set; }

    public DateTimeOffset StopAt { get; set; }

    public bool Ended { get; private set; }

    /// <summary>Ends it: the answer that streams stops, its previews can no longer run.</summary>
    public void End()
    {
        lock (Gate)
        {
            Ended = true;
            Previews.Clear();
            Runs.Clear();
            DocsOffers.Clear();
            Turns.Clear();
            Streaming?.Cancel();
        }
    }
}

/// <summary>An offer to look it up on axis.com: what the model would look up and when the offer ends.</summary>
internal sealed record AssistantDocsOffer(string Topic, DateTimeOffset Ends);

/// <summary>A preview the chat showed: its preview tool and input (again for the failed devices) and its run tool.</summary>
internal sealed record AssistantPreview(string PreviewTool, string RunTool, System.Text.Json.JsonElement Arguments, DateTimeOffset RanAt = default);

/// <summary>
/// The open chats (docs/spec/assistant.md "Limits"): one per login session (a new conversation id of the session ends the
/// one before), ended by End, by the session's end (sign-out, password change) and after 30 minutes without a message.
/// </summary>
internal sealed class AssistantConversations : IDisposable
{
    public static readonly TimeSpan Idle = TimeSpan.FromMinutes(30);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, AssistantConversation> _bySession = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly AuthTokenStore? _tokens;

    public AssistantConversations(TimeProvider time, AuthTokenStore? tokens = null)
    {
        _time = time;
        _tokens = tokens;
        if (_tokens is not null)
        {
            _tokens.SessionsEnded += OnSessionsEnded;
        }
    }

    /// <summary>Open chats (tests).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                Sweep();
                return _bySession.Count;
            }
        }
    }

    /// <summary>The chat <paramref name="id"/> of the session; a new one when the session has another (that one ends).</summary>
    public AssistantConversation Open(string sessionKey, Guid userId, string id)
    {
        AssistantConversation? ended = null;
        AssistantConversation conversation;
        lock (_gate)
        {
            Sweep();
            if (_bySession.TryGetValue(sessionKey, out var existing) && existing.Id == id && existing.UserId == userId && !existing.Ended)
            {
                existing.LastUsed = _time.GetUtcNow();
                return existing;
            }

            ended = existing;
            conversation = new AssistantConversation(sessionKey, userId, id, _time.GetUtcNow());
            _bySession[sessionKey] = conversation;
        }

        ended?.End();
        return conversation;
    }

    /// <summary>The chat <paramref name="id"/> of the session; null when it is not open.</summary>
    public AssistantConversation? Find(string sessionKey, string id)
    {
        lock (_gate)
        {
            Sweep();
            return _bySession.TryGetValue(sessionKey, out var conversation) && conversation.Id == id && !conversation.Ended ? conversation : null;
        }
    }

    /// <summary>Ends the chat <paramref name="id"/> of the session (End: the panel closed, New chat).</summary>
    public void End(string sessionKey, string id)
    {
        AssistantConversation? ended = null;
        lock (_gate)
        {
            if (_bySession.TryGetValue(sessionKey, out var conversation) && conversation.Id == id)
            {
                _bySession.Remove(sessionKey);
                ended = conversation;
            }
        }

        ended?.End();
    }

    /// <summary>Ends every chat (the assistant was turned off).</summary>
    public void EndAll()
    {
        List<AssistantConversation> all;
        lock (_gate)
        {
            all = [.. _bySession.Values];
            _bySession.Clear();
        }

        all.ForEach(c => c.End());
    }

    private void OnSessionsEnded(object? sender, EndedSessions e)
    {
        List<AssistantConversation> ended;
        lock (_gate)
        {
            ended = [.. _bySession.Values.Where(c => (e.TokenHash is not null && c.SessionKey == e.TokenHash) || (e.UserId is { } user && c.UserId == user))];
            foreach (var conversation in ended)
            {
                _bySession.Remove(conversation.SessionKey);
            }
        }

        ended.ForEach(c => c.End());
    }

    /// <summary>Ends the chats without a message for <see cref="Idle"/> (never one that streams).</summary>
    private void Sweep()
    {
        var now = _time.GetUtcNow();
        foreach (var idle in _bySession.Values.Where(c => c.Streaming is null && now - c.LastUsed > Idle).ToList())
        {
            _bySession.Remove(idle.SessionKey);
            idle.End();
        }
    }

    public void Dispose()
    {
        if (_tokens is not null)
        {
            _tokens.SessionsEnded -= OnSessionsEnded;
        }

        EndAll();
    }
}
