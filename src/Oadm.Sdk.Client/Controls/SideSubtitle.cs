using Avalonia.Controls;
using Avalonia.Media;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// The small grey side subtitle right of a title (page title bar, dialog title bar): both are bottom aligned, and
/// <see cref="Align"/> puts the subtitle on the title's baseline after arrange.
/// </summary>
public static class SideSubtitle
{
    /// <summary>Pixels the subtitle sits above the title baseline.</summary>
    public const double Raise = 2;

    /// <summary>
    /// Puts the subtitle on the title's baseline. Both are bottom aligned, but a large title line has more space below
    /// the baseline than a 12 px line, so bottom alignment alone leaves the subtitle a few pixels too low. The difference
    /// is applied as a whole-pixel render offset (no second layout pass, text stays crisp), then the subtitle is raised by
    /// <see cref="Raise"/>. Call it from <c>ArrangeOverride</c> after the base arrange.
    /// </summary>
    public static void Align(TextBlock title, TextBlock subtitle)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(subtitle);
        if (!subtitle.IsVisible || title.TextLayout.TextLines.Count == 0 || subtitle.TextLayout.TextLines.Count == 0)
        {
            return;
        }

        double titleBaseline = title.Bounds.Y + title.Padding.Top + title.TextLayout.TextLines[0].Baseline;
        double subtitleBaseline = subtitle.Bounds.Y + subtitle.Padding.Top + subtitle.TextLayout.TextLines[0].Baseline;
        double shift = Math.Round(titleBaseline - subtitleBaseline) - Raise;
        subtitle.RenderTransform = shift == 0 ? null : new TranslateTransform(0, shift);
    }
}
