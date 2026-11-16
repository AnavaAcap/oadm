using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Oadm.Plugins.VpnFleet.Tests;

public sealed class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new StyleInclude(new Uri("avares://Oadm.Plugins.VpnFleet.Tests/")) { Source = new Uri("avares://Oadm.Client/Themes/OadmTheme.axaml") });
    }
}

public static class HeadlessEntry
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HeadlessTestApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}

/// <summary>Headless helpers: the real host page around a view, render pumping and PNG capture (OADM_SCREENSHOT_DIR).</summary>
internal static class Headless
{
    public static Window Host(Control view)
    {
        var page = new Oadm.Client.Shell.CorePluginPageView
        {
            DataContext = new Oadm.Client.Shell.CorePluginPageViewModel(Fleet.PluginId, "VPN Fleet", view, ((Oadm.Sdk.Client.ICorePluginPage)new Client.VpnFleetPage()).HasOwnCards),
        };
        var window = new Window { Width = 1500, Height = 760, Content = new Border { Padding = new Thickness(16), Child = page } };
        Oadm.Client.App.ApplyCrispText(window);
        return window;
    }

    public static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    public static void Capture(Window window, string name)
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using (frame)
        {
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
                frame.Save(Path.Combine(outDir, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
        }
    }
}

/// <summary>The page's table as the server's events build it (rows events, then the state): for tests that wait for a read.</summary>
internal static class FleetEvents
{
    public static async Task<Oadm.Plugins.VpnFleet.FleetState> NextStateAsync(Oadm.Sdk.Testing.FakeCoreContext server, Func<Oadm.Plugins.VpnFleet.FleetState, bool> match)
    {
        var rows = new Dictionary<Guid, IReadOnlyList<Oadm.Plugins.VpnFleet.FleetRow>>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await foreach (var item in server.Published.Reader.ReadAllAsync(timeout.Token))
        {
            if (item.Topic == Oadm.Plugins.VpnFleet.Fleet.RowsTopic)
            {
                var change = System.Text.Json.JsonSerializer.Deserialize<Oadm.Plugins.VpnFleet.FleetRowsEvent>(item.PayloadJson!, Oadm.Plugins.VpnFleet.Fleet.Json)!;
                foreach (var id in change.Devices)
                {
                    rows.Remove(id);
                }

                foreach (var device in change.Rows.GroupBy(r => r.DeviceId))
                {
                    rows[device.Key] = [.. device];
                }

                continue;
            }

            var state = System.Text.Json.JsonSerializer.Deserialize<Oadm.Plugins.VpnFleet.FleetState>(item.PayloadJson!, Oadm.Plugins.VpnFleet.Fleet.Json)!;
            if (state.Cleared)
            {
                rows.Clear();
            }

            var whole = state with { Rows = [.. rows.Values.SelectMany(r => r).OrderBy(r => r.Address, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Vpn)] };
            if (match(whole))
            {
                return whole;
            }
        }

        throw new TimeoutException();
    }
}
