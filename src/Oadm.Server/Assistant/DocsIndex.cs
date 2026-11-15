using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Oadm.Server.Assistant;

/// <summary>One page of the user docs: its title, its address on oadm.app and its text.</summary>
internal sealed record DocsPage(string Title, string Url, string Text);

/// <summary>A hit of <see cref="DocsIndex.Search"/>: the page and its best passage.</summary>
internal sealed record DocsHit(string Title, string Url, string Passage, double Score);

/// <summary>
/// The search index of the user docs (docs/spec/assistant.md "Questions about OADM"): the pages of the website's user docs,
/// made at build time into the server's resources (<c>Assistant/DocsIndex.targets</c>). A simple ranked word search: each page
/// is cut into passages (a paragraph under its heading), every query word counts by how rare it is, words in the title and
/// the heading count more; at most <see cref="MaxHits"/> pages, one passage each.
/// </summary>
internal sealed class DocsIndex
{
    public const string ResourceName = "Oadm.Server.Assistant.docs-index.json";
    public const string Site = "https://oadm.app/";
    public const int MaxHits = 5;

    /// <summary>The longest passage of a hit (about 400 tokens).</summary>
    public const int MaxPassage = 1_600;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "can", "do", "does", "for", "from", "how", "i", "in", "is", "it", "my",
        "of", "on", "or", "the", "this", "to", "what", "when", "where", "which", "who", "why", "with", "you", "your", "oadm",
    };

    private static readonly Lazy<DocsIndex> Embedded = new(Load);

    private readonly List<Passage> _passages = [];
    private readonly Dictionary<string, int> _documentFrequency = new(StringComparer.Ordinal);

    public DocsIndex(IEnumerable<DocsPage> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        Pages = [.. pages];
        foreach (var page in Pages)
        {
            var heading = string.Empty;
            foreach (var block in page.Text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (block.StartsWith('#'))
                {
                    var lines = block.Split('\n', 2);
                    heading = lines[0].TrimStart('#').Trim();
                    if (lines.Length == 1)
                    {
                        continue;
                    }
                }

                var words = Words(block).ToList();
                if (words.Count == 0)
                {
                    continue;
                }

                var passage = new Passage(page, heading, block, Counts(words), Counts(Words(heading)), Counts(Words(page.Title)));
                _passages.Add(passage);
                foreach (var word in passage.Words.Keys)
                {
                    _documentFrequency[word] = _documentFrequency.GetValueOrDefault(word) + 1;
                }
            }
        }
    }

    /// <summary>The index of the docs shipped with this server.</summary>
    public static DocsIndex Shipped => Embedded.Value;

    public IReadOnlyList<DocsPage> Pages { get; }

    /// <summary>The best pages for <paramref name="query"/>, best first, one passage each; empty when no word matches.</summary>
    public IReadOnlyList<DocsHit> Search(string query)
    {
        var terms = Words(query ?? string.Empty).Where(w => !StopWords.Contains(w)).Distinct(StringComparer.Ordinal).Take(20).ToList();
        if (terms.Count == 0)
        {
            return [];
        }

        var total = Math.Max(1, _passages.Count);
        var weights = terms.ToDictionary(t => t, t => Math.Log(1 + (total / (1.0 + Frequency(t)))), StringComparer.Ordinal);
        var best = new Dictionary<DocsPage, (Passage Passage, double Score)>();
        foreach (var passage in _passages)
        {
            var score = 0.0;
            var matched = 0;
            foreach (var term in terms)
            {
                var inText = Matches(passage.Words, term);
                var inHeading = Matches(passage.HeadingWords, term);
                var inTitle = Matches(passage.TitleWords, term);
                if (inText + inHeading + inTitle == 0)
                {
                    continue;
                }

                matched++;
                score += weights[term] * (Math.Log(1 + inText) + (2 * Math.Min(1, inHeading)) + (3 * Math.Min(1, inTitle)));
            }

            if (matched == 0)
            {
                continue;
            }

            score *= (double)matched / terms.Count; // passages with all the words first
            if (!best.TryGetValue(passage.Page, out var current) || score > current.Score)
            {
                best[passage.Page] = (passage, score);
            }
        }

        return
        [
            .. best.OrderByDescending(b => b.Value.Score).ThenBy(b => b.Key.Url, StringComparer.Ordinal).Take(MaxHits)
                .Select(b => new DocsHit(b.Key.Title, b.Key.Url, Cut(b.Value.Passage), b.Value.Score)),
        ];
    }

    private int Frequency(string term) =>
        _documentFrequency.TryGetValue(term, out var exact) ? exact : _documentFrequency.Where(f => f.Key.StartsWith(term, StringComparison.Ordinal)).Sum(f => f.Value);

    /// <summary>How often <paramref name="term"/> or a longer word that starts with it ("upgrade" in "upgrades") occurs.</summary>
    private static int Matches(Dictionary<string, int> words, string term)
    {
        if (words.Count == 0)
        {
            return 0;
        }

        var count = words.GetValueOrDefault(term);
        if (term.Length >= 4)
        {
            foreach (var (word, n) in words)
            {
                if (word.Length > term.Length && word.StartsWith(term, StringComparison.Ordinal))
                {
                    count += n;
                }
            }
        }

        return count;
    }

    private static string Cut(Passage passage)
    {
        var text = passage.Heading.Length > 0 && !passage.Text.StartsWith('#') ? passage.Heading + ": " + passage.Text : passage.Text;
        return text.Length <= MaxPassage ? text : text[..MaxPassage].TrimEnd() + " ...";
    }

    private static Dictionary<string, int> Counts(IEnumerable<string> words)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var word in words)
        {
            counts[word] = counts.GetValueOrDefault(word) + 1;
        }

        return counts;
    }

    /// <summary>The lower-case words of <paramref name="text"/> (letters and digits, at least 2 characters).</summary>
    public static IEnumerable<string> Words(string text)
    {
        var word = new StringBuilder();
        foreach (var c in text + " ")
        {
            if (char.IsLetterOrDigit(c))
            {
                word.Append(char.ToLowerInvariant(c));
                continue;
            }

            if (word.Length >= 2)
            {
                yield return word.ToString();
            }

            word.Clear();
        }
    }

    private static DocsIndex Load()
    {
        using var stream = typeof(DocsIndex).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The docs index is not in the server's resources.");
        using var document = JsonDocument.Parse(stream);
        var pages = new List<DocsPage>();
        foreach (var page in document.RootElement.EnumerateArray())
        {
            var path = page.GetProperty("p").GetString() ?? string.Empty;
            pages.Add(new DocsPage(page.GetProperty("t").GetString() ?? path, Site + path + (path.Length == 0 ? string.Empty : "/"), page.GetProperty("x").GetString() ?? string.Empty));
        }

        return new DocsIndex(pages);
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Pages.Count} pages, {_passages.Count} passages");

    private sealed record Passage(DocsPage Page, string Heading, string Text, Dictionary<string, int> Words, Dictionary<string, int> HeadingWords, Dictionary<string, int> TitleWords);
}
