using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Oadm.Theme.Shell;

/// <summary>
/// Dims the window behind a dialog (docs/spec/visual-style.md "Dialogs"): while a window opened with
/// <see cref="Window.ShowDialog(Window)"/> is open, its owner shows <c>Oadm.DialogDimBrush</c> over everything, title bar
/// included, so the dialog stands out although both have the window background. One hook for every dialog of the client,
/// its plugins and the OADM Site Node app (<see cref="Attach"/>); a dialog of a dialog dims the first dialog too.
/// </summary>
public static class DialogDim
{
    /// <summary>The name of the dimming layer in the owner's overlay layer.</summary>
    public const string LayerName = "OadmDialogDim";

    private static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(120);
    private static bool attached;

    /// <summary>Dims the owner of every dialog that opens from now on (once per process).</summary>
    public static void Attach()
    {
        if (attached)
        {
            return;
        }

        attached = true;
        // WindowOpenedEvent comes before ShowDialog has set IsDialog and the owner (like WindowFit, wait for Opened).
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) =>
        {
            void OnOpened(object? sender, EventArgs e)
            {
                window.Opened -= OnOpened;
                if (window.IsDialog && window.Owner is Window owner)
                {
                    Dim(window, owner);
                }
            }

            window.Opened += OnOpened;
        });
    }

    private static void Dim(Window dialog, Window owner)
    {
        if (OverlayLayer.GetOverlayLayer(owner) is not { } layer)
        {
            return;
        }

        var brush = owner.TryFindResource("Oadm.DialogDimBrush", owner.ActualThemeVariant, out var found) && found is IBrush b
            ? b
            : new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0));
        var dim = new Border
        {
            Name = LayerName,
            Background = brush,
            IsHitTestVisible = false,
            Width = layer.Bounds.Width,
            Height = layer.Bounds.Height,
            Opacity = 0,
            Transitions = [new DoubleTransition { Property = Visual.OpacityProperty, Duration = Fade }],
        };

        void Resize(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Visual.BoundsProperty)
            {
                dim.Width = layer.Bounds.Width;
                dim.Height = layer.Bounds.Height;
            }
        }

        void Undim(object? sender, EventArgs e)
        {
            dialog.Closed -= Undim;
            layer.PropertyChanged -= Resize;
            layer.Children.Remove(dim);
        }

        layer.PropertyChanged += Resize;
        dialog.Closed += Undim;
        layer.Children.Add(dim);
        dim.Opacity = 1;
    }
}
