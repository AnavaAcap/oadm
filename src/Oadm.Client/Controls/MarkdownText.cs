using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Client.Controls;

/// <summary>
/// Markdown shown as text (the Assistant's answers): paragraphs, headings, lists, bold, italic, inline code, code blocks
/// (<see cref="CodeView"/>) and tables, from <see cref="MarkdownDocument"/>. Text is selectable. The look comes from the
/// theme (<c>StackPanel.markdown</c>, <c>:is(TextBlock).mdHeading</c>, <c>Border.mdCell</c>, <c>Border.mdHeaderCell</c>);
/// inline code uses <c>Oadm.FontFamilyMono</c>. Rebuilt when <see cref="Markdown"/> changes (an answer that streams).
/// </summary>
public sealed class MarkdownText : StackPanel
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownText, string?>(nameof(Markdown));

    static MarkdownText()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownText>((control, _) => control.Rebuild());
    }

    public MarkdownText()
    {
        Classes.Add("markdown");
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private void Rebuild()
    {
        Children.Clear();
        foreach (var block in MarkdownDocument.Parse(Markdown))
        {
            Children.Add(block switch
            {
                MarkdownHeading heading => Text(heading.Spans, "mdHeading"),
                MarkdownParagraph paragraph => Text(paragraph.Spans),
                MarkdownList list => List(list),
                MarkdownCode code => new CodeView
                {
                    Text = code.Text,
                    Language = code.Language.ToUpperInvariant() switch { "JSON" => CodeLanguage.Json, "XML" => CodeLanguage.Xml, _ => CodeLanguage.Plain },
                },
                MarkdownTable table => Table(table),
                _ => new TextBlock(),
            });
        }
    }

    private SelectableTextBlock Text(IReadOnlyList<MarkdownSpan> spans, string? style = null)
    {
        var text = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
        if (style is not null)
        {
            text.Classes.Add(style);
        }

        text.Inlines ??= [];
        foreach (var span in spans)
        {
            var run = new Run(span.Text);
            if (span.Bold)
            {
                run.FontWeight = FontWeight.SemiBold;
            }

            if (span.Italic)
            {
                run.FontStyle = FontStyle.Italic;
            }

            if (span.Code && this.TryFindResource("Oadm.FontFamilyMono", out var mono) && mono is FontFamily family)
            {
                run.FontFamily = family;
            }

            text.Inlines.Add(run);
        }

        return text;
    }

    private Grid List(MarkdownList list)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Classes = { "mdList" } };
        for (int i = 0; i < list.Items.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var marker = new TextBlock
            {
                Text = list.Ordered ? (list.Start + i).ToString(System.Globalization.CultureInfo.CurrentCulture) + "." : "•",
                Classes = { "mdMarker" },
            };
            Grid.SetRow(marker, i);
            var item = Text(list.Items[i]);
            Grid.SetRow(item, i);
            Grid.SetColumn(item, 1);
            grid.Children.Add(marker);
            grid.Children.Add(item);
        }

        return grid;
    }

    private ScrollViewer Table(MarkdownTable table)
    {
        int columns = table.Header.Count;
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left, Classes = { "mdTable" } };
        for (int c = 0; c < columns; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        }

        void Add(IReadOnlyList<MarkdownSpan> spans, int row, int column, bool header)
        {
            var text = Text(spans, header ? "mdHeader" : null);
            text.TextWrapping = TextWrapping.NoWrap;
            if (column < table.RightAligned.Count && table.RightAligned[column])
            {
                text.HorizontalAlignment = HorizontalAlignment.Right;
            }

            var cell = new Border { Child = text, Classes = { header ? "mdHeaderCell" : "mdCell" } };
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, column);
            grid.Children.Add(cell);
        }

        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (int c = 0; c < columns; c++)
        {
            Add(table.Header[c], 0, c, header: true);
        }

        for (int r = 0; r < table.Rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (int c = 0; c < columns; c++)
            {
                Add(table.Rows[r][c], r + 1, c, header: false);
            }
        }

        // Wide tables scroll sideways inside the answer instead of widening the panel.
        return new ScrollViewer
        {
            Content = grid,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
    }
}
