using System.Globalization;

namespace Oadm.Contracts.Vulnerabilities;

/// <summary>
/// The CVEs one device may be affected by (docs/spec/vulnerabilities.md): the Vulnerabilities column's text ("2 · High", the
/// possible ones apart: "+3 possible"), its sort key and its tooltip. Shared by every device with the same model, firmware and
/// chip; the client's column and the server's tools both use it, so both say the same for a device.
/// </summary>
public sealed class DeviceVulnerabilities
{
    public static readonly DeviceVulnerabilities None = new([]);

    /// <summary>The level of a possible match (tooltip, window).</summary>
    public const string PossibleLevel = "Possibly affected";

    public DeviceVulnerabilities(IReadOnlyList<DeviceVulnerability> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items = items;
        Ids = items.Select(i => i.Vulnerability.Id).ToHashSet(StringComparer.Ordinal);
        Certain = items.Count(i => !i.Possible); // the matcher sorts the certain ones first, worst first
        PossibleCount = items.Count - Certain;
        var worst = Certain == 0 ? null : items[0].Vulnerability;
        Text = worst is null ? "" : $"{Certain} · {(worst.Severity.Length > 0 ? worst.Severity : "Unrated")}";
        PossibleText = PossibleCount == 0 ? "" : Certain > 0 ? $"+{PossibleCount} possible" : $"{PossibleCount} possible";
        SortKey = ((worst?.SeverityRank ?? 0) * 1_000_000) + (Math.Min(Certain, 999) * 1_000) + Math.Min(PossibleCount, 999);
        Tooltip = items.Count == 0 ? null : string.Join(Environment.NewLine, items.Take(MaxTooltipLines).Select(Line))
            + (items.Count > MaxTooltipLines ? $"{Environment.NewLine}and {items.Count - MaxTooltipLines} more" : "");
    }

    /// <summary>Lines of the tooltip; the rest is counted.</summary>
    public const int MaxTooltipLines = 12;

    /// <summary>The CVEs, the certain ones first (worst first), then the possible ones.</summary>
    public IReadOnlyList<DeviceVulnerability> Items { get; }

    /// <summary>The CVE ids, certain and possible (search by id).</summary>
    public IReadOnlySet<string> Ids { get; }

    /// <summary>CVEs the device is affected by as far as the list says (counted in the column).</summary>
    public int Certain { get; }

    /// <summary>CVEs it may be affected by on its LTS track (<see cref="DeviceVulnerability.Possible"/>), not counted.</summary>
    public int PossibleCount { get; }

    /// <summary>"2 · High" for the certain ones; empty when there are none.</summary>
    public string Text { get; }

    /// <summary>"+3 possible" next to <see cref="Text"/>, "3 possible" alone; empty when there are none.</summary>
    public string PossibleText { get; }

    public bool HasPossible => PossibleCount > 0;

    /// <summary>
    /// Highest certain severity first, then the most certain CVEs, then the most possible ones (the column sorts descending by
    /// it): a device with only possible CVEs sorts below every device with a certain one.
    /// </summary>
    public int SortKey { get; }

    /// <summary>One line per CVE: "CVE-2024-6509 · Medium · fixed in 11.11.93".</summary>
    public string? Tooltip { get; }

    /// <summary>Any CVE, certain or possible (tooltip, context menu, window).</summary>
    public bool Any => Items.Count > 0;

    /// <summary>The worst certain one is High or Critical (red chip).</summary>
    public bool IsSevere => Certain > 0 && Items[0].Vulnerability.SeverityRank >= 3;

    /// <summary>The worst certain one is Medium (amber chip).</summary>
    public bool IsModerate => Certain > 0 && Items[0].Vulnerability.SeverityRank == 2;

    /// <summary>The worst certain one is Low or unrated (plain text).</summary>
    public bool IsMinor => Certain > 0 && Items[0].Vulnerability.SeverityRank <= 1;

    /// <summary>The level text of a match: its severity ("Unrated" for none) or <see cref="PossibleLevel"/>.</summary>
    public static string LevelOf(DeviceVulnerability item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Possible ? PossibleLevel : item.Vulnerability.Severity.Length > 0 ? item.Vulnerability.Severity : "Unrated";
    }

    /// <summary>"11.11.93"; for a possible match "11.11 (check the advisory for your LTS)".</summary>
    public static string FixedText(string fixedIn, bool possible) => possible ? $"{fixedIn} (check the advisory for your LTS)" : fixedIn;

    /// <summary>The tooltip's last column: "fixed in 11.11.93", "no fixed version listed".</summary>
    public static string FixText(DeviceVulnerability item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.FixedIn is { } fixedIn ? $"fixed in {FixedText(fixedIn, item.Possible)}" : "no fixed version listed";
    }

    private static string Line(DeviceVulnerability item) =>
        string.Create(CultureInfo.InvariantCulture, $"{item.Vulnerability.Id} · {LevelOf(item)} · {FixText(item)}");
}
