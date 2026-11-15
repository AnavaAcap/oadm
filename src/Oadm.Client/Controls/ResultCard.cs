using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Metadata;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Client.Controls;

/// <summary>
/// A result in the Assistant's answers (docs/spec/visual-style.md "Shared controls"): a card with a header line (icon, title,
/// optional subtitle, and the card's actions on the right as flat command or icon buttons), its content below and an optional
/// footer ("+38 more"). The device tables, the Markdown tables and the preview cards of the Assistant. The look comes from
/// the theme (<c>Border.resultCard</c>, <c>Grid.resultCardHeader</c>, <c>TextBlock.resultCardTitle</c>).
/// </summary>
public sealed class ResultCard : Border
{
    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<ResultCard, Geometry?>(nameof(Icon));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<ResultCard, string?>(nameof(Title));

    public static readonly StyledProperty<string?> SubtitleProperty =
        AvaloniaProperty.Register<ResultCard, string?>(nameof(Subtitle));

    public static readonly StyledProperty<Control?> BodyProperty =
        AvaloniaProperty.Register<ResultCard, Control?>(nameof(Body));

    public static readonly StyledProperty<Control?> FooterProperty =
        AvaloniaProperty.Register<ResultCard, Control?>(nameof(Footer));

    private readonly OadmIcon _icon = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _subtitle = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentControl _body = new();
    private readonly ContentControl _footer = new();

    public ResultCard()
    {
        Classes.Add("resultCard");
        _title.Classes.Add("resultCardTitle");
        _subtitle.Classes.Add("secondary");
        _actions.Classes.Add("resultCardActions");
        _body.Classes.Add("resultCardBody");
        _footer.Classes.Add("resultCardFooter");
        var titles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Children = { _title, _subtitle } };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Classes = { "resultCardHeader" } };
        Grid.SetColumn(titles, 1);
        Grid.SetColumn(_actions, 2);
        header.Children.Add(_icon);
        header.Children.Add(titles);
        header.Children.Add(_actions);
        Child = new StackPanel { Children = { header, _body, _footer } };
        Update();
    }

    /// <summary>The icon left of the title (the accent colour).</summary>
    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>"7 devices", "Restart".</summary>
    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>A dim word right of the title ("3 devices" of a preview); optional.</summary>
    public string? Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    /// <summary>The card's actions in its header (Select all in the grid, Copy as CSV, Save as CSV).</summary>
    public Avalonia.Controls.Controls Actions => _actions.Children;

    /// <summary>The content below the header (the table, the plan).</summary>
    [Content]
    public Control? Body
    {
        get => GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    /// <summary>A line below the content ("+38 more"); optional.</summary>
    public Control? Footer
    {
        get => GetValue(FooterProperty);
        set => SetValue(FooterProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IconProperty || change.Property == TitleProperty || change.Property == SubtitleProperty
            || change.Property == BodyProperty || change.Property == FooterProperty)
        {
            Update();
        }
    }

    private void Update()
    {
        _icon.Data = Icon;
        _icon.IsVisible = Icon is not null;
        _title.Text = Title;
        ToolTip.SetTip(_title, Title);
        _subtitle.Text = Subtitle;
        _subtitle.IsVisible = !string.IsNullOrEmpty(Subtitle);
        _body.Content = Body;
        _body.IsVisible = Body is not null;
        _footer.Content = Footer;
        _footer.IsVisible = Footer is not null;
    }
}
