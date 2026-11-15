using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Client.Controls;

/// <summary>A word of a Markdown text that is a link (the Assistant: a device): <see cref="Key"/> goes to the actions.</summary>
public readonly record struct MarkdownLink(int Start, int Length, string Key);

/// <summary>One entry of a link's menu.</summary>
public sealed record MarkdownLinkAction(string Text, string IconKey, Action Run);

/// <summary>
/// What a <see cref="MarkdownText"/> offers besides the text (the Assistant's answers): links in the text (a click opens,
/// the right button shows the menu) and Copy as CSV / Save as CSV below each table.
/// </summary>
public interface IMarkdownActions
{
    /// <summary>The links in one piece of text (positions in it).</summary>
    IReadOnlyList<MarkdownLink> FindLinks(string text);

    /// <summary>A click on a link.</summary>
    void OpenLink(string key);

    /// <summary>The link's menu (right button).</summary>
    IReadOnlyList<MarkdownLinkAction> LinkMenu(string key);

    /// <summary>Copy as CSV below a table.</summary>
    void CopyTable(MarkdownTable table);

    /// <summary>Save as CSV below a table.</summary>
    void SaveTable(MarkdownTable table);
}

/// <summary>
/// Markdown shown as text (the Assistant's answers): paragraphs, headings, lists, bold, italic, inline code, code blocks
/// (<see cref="CodeView"/>) and tables, from <see cref="MarkdownDocument"/>. Text is selectable. The look comes from the
/// theme (<c>StackPanel.markdown</c>, <c>:is(TextBlock).mdHeading</c>, <c>Border.mdCell</c>, <c>Border.mdHeaderCell</c>);
/// inline code uses <c>Oadm.FontFamilyMono</c>. When <see cref="Markdown"/> changes (an answer that streams) only the blocks
/// that changed are built again (usually the last one).
/// </summary>
public sealed class MarkdownText : StackPanel
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownText, string?>(nameof(Markdown));

    public static readonly StyledProperty<IMarkdownActions?> ActionsProperty =
        AvaloniaProperty.Register<MarkdownText, IMarkdownActions?>(nameof(Actions));

    /// <summary>The tooltips of the buttons below a table.</summary>
    public const string CopyCsv = "Copy as CSV", SaveCsv = "Save as CSV";

    static MarkdownText()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownText>((control, _) => control.Rebuild());
        ActionsProperty.Changed.AddClassHandler<MarkdownText>((control, _) =>
        {
            control._keys = []; // every block again: with or without links and table buttons
            control.Rebuild();
        });
    }

    /// <summary>Links and table buttons (the Assistant); null: plain text.</summary>
    public IMarkdownActions? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
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

    private List<string> _keys = [];

    /// <summary>How many blocks were built since the control was made (tests: a growing answer builds only what changed).</summary>
    internal int BuiltBlocks { get; private set; }

    private void Rebuild()
    {
        var blocks = MarkdownDocument.Parse(Markdown);
        var keys = blocks.Select(MarkdownDocument.Key).ToList();
        int same = 0;
        while (same < keys.Count && same < _keys.Count && same < Children.Count && keys[same] == _keys[same])
        {
            same++;
        }

        Children.RemoveRange(same, Children.Count - same);
        foreach (var block in blocks.Skip(same))
        {
            BuiltBlocks++;
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

        _keys = keys;
    }

    private SelectableTextBlock Text(IReadOnlyList<MarkdownSpan> spans, string? style = null)
    {
        var text = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
        if (style is not null)
        {
            text.Classes.Add(style);
        }

        text.Inlines ??= [];
        List<(int Start, int End, string Key)>? links = null;
        int position = 0;
        foreach (var (span, piece, link) in Pieces(spans))
        {
            var run = new Run(piece);
            if (link is { } key)
            {
                (links ??= []).Add((position, position + piece.Length, key));
                if (this.TryFindResource("Oadm.TealBrush", out var teal) && teal is IBrush linkBrush)
                {
                    run.Foreground = linkBrush;
                }
            }

            position += piece.Length;
            if (span.Bold)
            {
                run.FontWeight = FontWeight.SemiBold;
            }

            if (span.Italic)
            {
                run.FontStyle = FontStyle.Italic;
            }

            if (span.Code)
            {
                // Inline code: monospace on a subtle background, same size; the theme's line height keeps the line as tall.
                if (this.TryFindResource("Oadm.FontFamilyMono", out var mono) && mono is FontFamily family)
                {
                    run.FontFamily = family;
                }

                if (this.TryFindResource("Oadm.SurfaceRaisedBrush", out var shade) && shade is IBrush brush)
                {
                    run.Background = brush;
                }
            }

            text.Inlines.Add(run);
        }

        if (links is not null)
        {
            text.Cursor = new Cursor(StandardCursorType.Hand);
            text.AddHandler(PointerReleasedEvent, (_, e) => OnLinkPointer(text, links, e), handledEventsToo: true);
        }

        return text;
    }

    /// <summary>The spans cut at the links <see cref="Actions"/> finds in them: (span, text, link key or null).</summary>
    private IEnumerable<(MarkdownSpan Span, string Text, string? Link)> Pieces(IReadOnlyList<MarkdownSpan> spans)
    {
        foreach (var span in spans)
        {
            var found = Actions?.FindLinks(span.Text) ?? [];
            int at = 0;
            foreach (var link in found.OrderBy(l => l.Start))
            {
                if (link.Start < at || link.Start + link.Length > span.Text.Length)
                {
                    continue;
                }

                if (link.Start > at)
                {
                    yield return (span, span.Text[at..link.Start], null);
                }

                yield return (span, span.Text.Substring(link.Start, link.Length), link.Key);
                at = link.Start + link.Length;
            }

            if (at < span.Text.Length || span.Text.Length == 0)
            {
                yield return (span, span.Text[at..], null);
            }
        }
    }

    /// <summary>A click on a link opens it; the right button shows its menu. A selection being made is left alone.</summary>
    private void OnLinkPointer(SelectableTextBlock text, List<(int Start, int End, string Key)> links, PointerReleasedEventArgs e)
    {
        if (Actions is not { } actions || text.SelectionEnd != text.SelectionStart || text.TextLayout is not { } layout)
        {
            return;
        }

        var point = e.GetPosition(text) - new Point(text.Padding.Left, text.Padding.Top);
        var hit = layout.HitTestPoint(point);
        if (!hit.IsInside)
        {
            return;
        }

        int index = hit.TextPosition;
        var link = links.FirstOrDefault(l => index >= l.Start && index < l.End);
        if (link.Key is null)
        {
            return;
        }

        e.Handled = true;
        if (e.InitialPressMouseButton == MouseButton.Right)
        {
            var menu = new ContextMenu();
            foreach (var action in actions.LinkMenu(link.Key))
            {
                var item = new MenuItem { Header = action.Text };
                if (this.TryFindResource("Icon." + action.IconKey, out var icon) && icon is Geometry geometry)
                {
                    item.Icon = new OadmIcon { Data = geometry };
                }

                item.Click += (_, _) => action.Run();
                menu.Items.Add(item);
            }

            menu.Open(text);
        }
        else if (e.InitialPressMouseButton == MouseButton.Left)
        {
            actions.OpenLink(link.Key);
        }
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

    private Control Table(MarkdownTable table)
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
        var scroller = new ScrollViewer
        {
            Content = grid,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        if (Actions is not { } actions)
        {
            return scroller;
        }

        // Copy as CSV and Save as CSV below the table (the Assistant).
        var buttons = new StackPanel { Classes = { "chatActions" } };
        buttons.Children.Add(IconButton("copy", CopyCsv, () => actions.CopyTable(table)));
        buttons.Children.Add(IconButton("download", SaveCsv, () => actions.SaveTable(table)));
        return new StackPanel { Classes = { "chatItem" }, Children = { scroller, buttons } };
    }

    private Button IconButton(string iconKey, string tip, Action run)
    {
        var button = new Button { Classes = { "toolbar", "iconOnly" } };
        if (this.TryFindResource("Icon." + iconKey, out var icon) && icon is Geometry geometry)
        {
            button.Content = new OadmIcon { Data = geometry };
        }

        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => run();
        return button;
    }
}
