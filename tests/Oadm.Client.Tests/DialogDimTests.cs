using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

using Microsoft.Extensions.DependencyInjection;

using Oadm.Client.Shell;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Testing;
using Oadm.Theme.Shell;

namespace Oadm.Client.Tests;

/// <summary>The window behind an open dialog is dimmed (docs/spec/visual-style.md "Dialogs").</summary>
public sealed class DialogDimTests
{
    [Fact]
    public async Task A_dialog_dims_its_owner_until_it_closes()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        bool done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var app = (App)Application.Current!;
            var window = new MainWindow { DataContext = app.Services!.GetRequiredService<MainWindowViewModel>(), Width = 1400, Height = 800 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            OverlayLayer layer = OverlayLayer.GetOverlayLayer(window)!;
            Assert.DoesNotContain(layer.Children, c => c.Name == DialogDim.LayerName);

            Task<bool> confirm = MessageWindow.ConfirmAsync(window, "Delete access key", "Agents with this key can no longer connect.", "Delete");
            await Eventually.TrueAsync(() => { Dispatcher.UIThread.RunJobs(); return window.OwnedWindows.Count == 1; }, "the dialog opens");
            Window dialog = window.OwnedWindows[0];
            await Eventually.TrueAsync(() => layer.Children.Any(c => c.Name == DialogDim.LayerName), "the owner is dimmed");
            Control dim = layer.Children.Single(c => c.Name == DialogDim.LayerName);
            Assert.False(dim.IsHitTestVisible);
            Assert.Equal(layer.Bounds.Size, new Size(dim.Width, dim.Height));
            Save(window, outDir, "dialog-dim-owner.png");
            Save(dialog, outDir, "dialog-dim-dialog.png");

            dialog.Close();
            Assert.False(await confirm);
            Assert.DoesNotContain(layer.Children, c => c.Name == DialogDim.LayerName);
            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task A_window_shown_without_ShowDialog_dims_nothing()
    {
        await HeadlessSession.Shared.Dispatch(() =>
        {
            var owner = new Window { Width = 600, Height = 400 };
            owner.Show();
            var tool = new Window { Width = 200, Height = 100 };
            tool.Show(owner);
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(OverlayLayer.GetOverlayLayer(owner)!.Children, c => c.Name == DialogDim.LayerName);
            tool.Close();
            owner.Close();
        }, CancellationToken.None);
    }

    private static void Save(Window window, string? outDir, string name)
    {
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            frame.Save(Path.Combine(outDir, name), PngBitmapEncoderOptions.Default);
        }
    }
}
