using System.Text;

namespace Oadm.Server.Assistant;

/// <summary>
/// The follow-up questions of an answer (docs/spec/assistant.md "What the panel shows"): the model ends an answer with a line
/// <c>&lt;&lt;next&gt;&gt; question 1 | question 2</c>; this filter takes that line out of the streamed text, which may cut it
/// anywhere, and keeps the questions (at most <see cref="MaxQuestions"/>, plain text). Only a marker at the start of a line
/// counts (spaces before it allowed); the same characters inside a line stay text. The text is held back only while the
/// start of a line could still become the marker.
/// </summary>
internal sealed class FollowUpFilter
{
    public const string Marker = "<<next>>";

    /// <summary>The most questions the panel shows.</summary>
    public const int MaxQuestions = 3;

    /// <summary>The longest question kept (a longer one is cut).</summary>
    public const int MaxQuestionLength = 200;

    private readonly StringBuilder _pending = new();
    private readonly StringBuilder _markerLine = new();
    private bool _atLineStart = true;
    private bool _inMarker;

    /// <summary>The questions of the last marker line seen (empty without one).</summary>
    public IReadOnlyList<string> Questions { get; private set; } = [];

    /// <summary>The next piece of the stream; returns the text to show now (may be empty).</summary>
    public string Feed(string piece)
    {
        _pending.Append(piece);
        return Process(final: false);
    }

    /// <summary>The stream (a round of the answer) ended: everything held back is text, an open marker line ends here.</summary>
    public string Finish()
    {
        var shown = Process(final: true);
        if (_inMarker)
        {
            EndMarker();
        }

        _atLineStart = true; // the next round starts a new line
        return shown;
    }

    private string Process(bool final)
    {
        var shown = new StringBuilder();
        while (_pending.Length > 0)
        {
            var text = _pending.ToString();
            if (_inMarker)
            {
                var end = text.IndexOf('\n', StringComparison.Ordinal);
                if (end < 0)
                {
                    _markerLine.Append(text);
                    _pending.Clear();
                    break;
                }

                _markerLine.Append(text, 0, end);
                _pending.Remove(0, end + 1);
                EndMarker();
                _atLineStart = true;
                continue;
            }

            if (_atLineStart)
            {
                var start = text.TrimStart(' ', '\t');
                if (start.StartsWith(Marker, StringComparison.OrdinalIgnoreCase))
                {
                    _inMarker = true;
                    _markerLine.Clear();
                    _pending.Clear().Append(start.AsSpan(Marker.Length));
                    continue;
                }

                if (!final && Marker.StartsWith(start, StringComparison.OrdinalIgnoreCase))
                {
                    break; // the start of the line may still become the marker: wait for more
                }

                _atLineStart = false;
            }

            var newline = text.IndexOf('\n', StringComparison.Ordinal);
            if (newline < 0)
            {
                shown.Append(text);
                _pending.Clear();
            }
            else
            {
                shown.Append(text, 0, newline + 1);
                _pending.Remove(0, newline + 1);
                _atLineStart = true;
            }
        }

        return shown.ToString();
    }

    private void EndMarker()
    {
        _inMarker = false;
        Questions = Parse(_markerLine.ToString());
        _markerLine.Clear();
    }

    /// <summary>The questions of a marker line: split at |, trimmed, no empty or repeated ones, at most three.</summary>
    internal static IReadOnlyList<string> Parse(string line) =>
    [
        .. line.Split('|').Select(q => q.Trim()).Where(q => q.Length > 0)
            .Select(q => q.Length <= MaxQuestionLength ? q : q[..MaxQuestionLength].TrimEnd())
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxQuestions),
    ];
}
