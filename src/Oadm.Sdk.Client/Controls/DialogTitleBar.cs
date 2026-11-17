using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// The header of every dialog window (a window with <c>WindowDecorations="BorderOnly"</c>, no system title bar): the
/// title as a heading, an optional small grey <see cref="Subtitle"/> right of it (like a page title) and an X that
/// closes the window, aligned with the content. Like a dialog on a web page it does not move and is modal. Padding comes
/// from the theme (<c>ui|DialogTitleBar</c>).
/// </summary>
public sealed class DialogTitleBar : Border
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<DialogTitleBar, string?>(nameof(Text));

    public static readonly StyledProperty<string?> SubtitleProperty =
        AvaloniaProperty.Register<DialogTitleBar, string?>(nameof(Subtitle));

    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Bottom };
    private readonly TextBlock _subtitle = new() { VerticalAlignment = VerticalAlignment.Bottom, IsVisible = false };

    public DialogTitleBar()
    {
        _text.Classes.Add("cardTitle");
        _subtitle.Classes.Add("pageSubtitle");
        var close = new Button { VerticalAlignment = VerticalAlignment.Center };
        close.Classes.Add("toolbar");
        close.Classes.Add("iconOnly");
        close.Content = new OadmIcon { Data = ToolbarButton.ResolveIcon("close") };
        ToolTip.SetTip(close, "Close");
        close.Click += (_, _) => (TopLevel.GetTopLevel(this) as Window)?.Close();
        Grid.SetColumn(close, 1);
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(_text);
        left.Children.Add(_subtitle);
        Child = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { left, close } };
    }

    /// <summary>The dialog title.</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>One short grey line right of the title; optional.</summary>
    public string? Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            _text.Text = Text;
        }
        else if (change.Property == SubtitleProperty)
        {
            _subtitle.Text = Subtitle;
            _subtitle.IsVisible = !string.IsNullOrEmpty(Subtitle);
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var arranged = base.ArrangeOverride(finalSize);
        SideSubtitle.Align(_text, _subtitle);
        return arranged;
    }
}
