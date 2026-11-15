namespace Oadm.Server.Assistant;

/// <summary>
/// The tokens of today (docs/spec/assistant.md "Settings", Daily limit): for the whole server and per user, in memory only,
/// counted on <see cref="TimeProvider"/> and started again at midnight server time (the local date). A restart starts at 0.
/// </summary>
internal sealed class AssistantUsage(TimeProvider time)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, long> _users = [];
    private DateOnly _day;
    private long _server;

    /// <summary>The tokens of today for the whole server.</summary>
    public long UsedToday()
    {
        lock (_gate)
        {
            Roll();
            return _server;
        }
    }

    /// <summary>The tokens of today of <paramref name="userId"/>.</summary>
    public long UsedToday(Guid userId)
    {
        lock (_gate)
        {
            Roll();
            return _users.GetValueOrDefault(userId);
        }
    }

    /// <summary>True when the server or the user reached today's limit (<paramref name="userLimit"/> 0: none per user).</summary>
    public bool Reached(Guid userId, long serverLimit, long userLimit)
    {
        lock (_gate)
        {
            Roll();
            return _server >= serverLimit || (userLimit > 0 && _users.GetValueOrDefault(userId) >= userLimit);
        }
    }

    public void Add(Guid userId, long tokens)
    {
        if (tokens <= 0)
        {
            return;
        }

        lock (_gate)
        {
            Roll();
            _server += tokens;
            _users[userId] = _users.GetValueOrDefault(userId) + tokens;
        }
    }

    /// <summary>
    /// Settles an answer: its tokens replace the estimate <paramref name="reserved"/> counted at its start (never below 0, also
    /// when the day changed meanwhile).
    /// </summary>
    public void Settle(Guid userId, long reserved, long tokens)
    {
        lock (_gate)
        {
            Roll();
            var change = Math.Max(0, tokens) - Math.Max(0, reserved);
            _server = Math.Max(0, _server + change);
            _users[userId] = Math.Max(0, _users.GetValueOrDefault(userId) + change);
        }
    }

    private void Roll()
    {
        var today = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
        if (today != _day)
        {
            _day = today;
            _server = 0;
            _users.Clear();
        }
    }
}
