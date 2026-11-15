using System.Windows.Input;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Client.Controls;

/// <summary>
/// A file as a small card (docs/spec/visual-style.md "Shared controls"): the file icon, the name (cut with an ellipsis) and the
/// size, and an X when <see cref="RemoveCommand"/> is set. The files attached in the Assistant's message box and the files of a
/// sent message. Theme <c>Border.chatChip</c>.
/// </summary>
public sealed class FileChip : Border
{
    public static readonly StyledProperty<string?> FileNameProperty =
        AvaloniaProperty.Register<FileChip, string?>(nameof(FileName));

    public static readonly StyledProperty<string?> SizeTextProperty =
        AvaloniaProperty.Register<FileChip, string?>(nameof(SizeText));

    public static readonly StyledProperty<ICommand?> RemoveCommandProperty =
        AvaloniaProperty.Register<FileChip, ICommand?>(nameof(RemoveCommand));

    public static readonly StyledProperty<object?> RemoveCommandParameterProperty =
        AvaloniaProperty.Register<FileChip, object?>(nameof(RemoveCommandParameter));

    public static readonly StyledProperty<string?> RemoveTipProperty =
        AvaloniaProperty.Register<FileChip, string?>(nameof(RemoveTip));

    private readonly OadmIcon _icon = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _name = new() { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 240, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _size = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _remove = new() { VerticalAlignment = VerticalAlignment.Center };

    public FileChip()
    {
        Classes.Add("chatChip");
        _size.Classes.Add("secondary");
        _remove.Classes.Add("toolbar");
        _remove.Classes.Add("iconOnly");
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children = { _icon, _name, _size, _remove } };
        Child = panel;
        AttachedToLogicalTree += (_, _) => UpdateIcons();
        Update();
    }

    public string? FileName
    {
        get => GetValue(FileNameProperty);
        set => SetValue(FileNameProperty, value);
    }

    /// <summary>"48 KB".</summary>
    public string? SizeText
    {
        get => GetValue(SizeTextProperty);
        set => SetValue(SizeTextProperty, value);
    }

    /// <summary>The X takes the file off; null: no X (a sent message).</summary>
    public ICommand? RemoveCommand
    {
        get => GetValue(RemoveCommandProperty);
        set => SetValue(RemoveCommandProperty, value);
    }

    public object? RemoveCommandParameter
    {
        get => GetValue(RemoveCommandParameterProperty);
        set => SetValue(RemoveCommandParameterProperty, value);
    }

    /// <summary>The X's tooltip ("Remove file").</summary>
    public string? RemoveTip
    {
        get => GetValue(RemoveTipProperty);
        set => SetValue(RemoveTipProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FileNameProperty || change.Property == SizeTextProperty || change.Property == RemoveCommandProperty
            || change.Property == RemoveCommandParameterProperty || change.Property == RemoveTipProperty)
        {
            Update();
        }
    }

    private void Update()
    {
        _name.Text = FileName;
        ToolTip.SetTip(_name, FileName);
        _size.Text = SizeText;
        _size.IsVisible = !string.IsNullOrEmpty(SizeText);
        _remove.Command = RemoveCommand;
        _remove.CommandParameter = RemoveCommandParameter;
        _remove.IsVisible = RemoveCommand is not null;
        ToolTip.SetTip(_remove, RemoveTip);
        Classes.Set("fixed", RemoveCommand is null);
    }

    private void UpdateIcons()
    {
        if (this.TryFindResource("Icon.file", out var file) && file is Geometry fileIcon)
        {
            _icon.Data = fileIcon;
        }

        if (this.TryFindResource("Icon.close", out var close) && close is Geometry closeIcon)
        {
            _remove.Content = new OadmIcon { Data = closeIcon };
        }
    }
}
