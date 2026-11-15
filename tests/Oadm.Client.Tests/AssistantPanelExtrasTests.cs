using System.Diagnostics;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Assistant;
using Oadm.Client.Controls;
using Oadm.Client.Devices;
using Oadm.Client.Dialogs;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;
using DeviceLimits = Oadm.Sdk.Devices.DeviceLimits;
using Oadm.Sdk.Plugins;

using Xunit.Abstractions;

using static Oadm.Client.Tests.AssistantTests;

namespace Oadm.Client.Tests;

/// <summary>
/// The Assistant panel's second part (docs/spec/assistant.md "The panel"): the empty panel's live counts and suggestions,
/// devices chosen with @, device links in answers, Try the failed devices again, Copy as CSV / Save as CSV, Copy chat and
/// Ctrl+J; headless screenshots with the prefix assistant-ux2-.
/// </summary>
public sealed class AssistantPanelExtrasTests(ITestOutputHelper output)
{
    private static Device[] Fleet(int count, int offline) =>
        [.. Enumerable.Range(1, count).Select(i => TestSupport.Device(Guid.NewGuid().ToString(), $"ACCC8E{i:X6}", $"10.1.{i / 250}.{i % 250 + 1}", "P3265-LVE",
            i <= offline ? DeviceStatus.Unreachable : DeviceStatus.Ok))];

    private static IDialogService Picker(Func<DevicePickerViewModel, bool> choose)
    {
        var dialogs = Substitute.For<IDialogService>();
        dialogs.ShowDevicePickerAsync(Arg.Any<DevicePickerViewModel>()).Returns(ci => Task.FromResult(choose(ci.Arg<DevicePickerViewModel>())));
        return dialogs;
    }

    [Fact]
    public async Task TheEmptyPanelCountsTheDevicesAndSuggestsForTheSelectionThePageAndTheRole()
    {
        var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5), seedSampleData: false);
        using var f = new DevicesFixture(api);
        var devices = Fleet(20, offline: 3);
        f.SeedDevices(devices);
        var panel = await PanelAsync(api, f);

        Assert.Equal([("20 devices", AssistantTexts.AskSummary), ("3 offline", AssistantTexts.AskOffline)], panel.StartChips.Select(c => (c.Text, c.Question)));
        Assert.Equal(AssistantTexts.Suggestions, panel.StartCards.Select(c => c.Text)); // nothing selected, no page: the general four

        // A change of the store follows per device, without a recount.
        f.Store.Apply(new DeviceChanged { Kind = DeviceChanged.Types.Kind.Updated, Device = TestSupport.Device(devices[5].Id, devices[5].Serial, devices[5].Address, "P3265-LVE", DeviceStatus.Unreachable) });
        f.Store.Apply(new DeviceChanged { Kind = DeviceChanged.Types.Kind.Removed, Device = devices[0] });
        Assert.Equal(["19 devices", "3 offline"], panel.StartChips.Select(c => c.Text));

        f.Select(devices[7].Id);
        Assert.Equal([AssistantTexts.RestartSelected(1), AssistantTexts.PictureOfSelected, AssistantTexts.FirmwareOfSelected, AssistantTexts.AskOffline],
            panel.StartCards.Select(c => c.Text));
        f.Select(devices[7].Id, devices[8].Id);
        panel.CurrentPageKey = "plugin:oadm.pki";
        Assert.Equal([AssistantTexts.RestartSelected(2), AssistantTexts.FirmwareOfSelected, AssistantTexts.ExpiringCertificates, AssistantTexts.HttpsOff],
            panel.StartCards.Select(c => c.Text));
        Assert.True(panel.StartCards.Count <= AssistantPanelViewModel.MaxStartCards);

        await panel.SendSuggestionCommand.ExecuteAsync(panel.StartChips[1].Question);
        Assert.Equal(AssistantTexts.AskOffline, api.AssistantMessages[^1].Text);
    }

    [Fact]
    public async Task ARoleWithoutTaskRightsGetsNoChangeSuggestions()
    {
        var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5), seedSampleData: false);
        var viewer = Session(admin: false, HostPermissionKeys.AssistantUse);
        using var f = new DevicesFixture(api, session: viewer);
        var devices = Fleet(3, offline: 0);
        f.SeedDevices(devices);
        var panel = await PanelAsync(api, f, viewer);
        f.Select(devices[0].Id, devices[1].Id);
        Assert.DoesNotContain(panel.StartCards, c => c.IconKey == "restart");
        Assert.Contains(panel.StartCards, c => c.Text == AssistantTexts.FirmwareOfSelected);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public async Task CountsAndLinksAt100000DevicesFollowAChangeWithoutARecount()
    {
        var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5), seedSampleData: false);
        using var f = new DevicesFixture(api);
        var devices = Fleet(DeviceLimits.MaxDevices, offline: 1_000);
        f.SeedDevices(devices);
        var watch = Stopwatch.StartNew();
        var panel = await PanelAsync(api, f);
        var open = watch.Elapsed;
        Assert.Equal(AssistantTexts.DevicesChip(DeviceLimits.MaxDevices), panel.StartChips[0].Text);

        watch.Restart();
        for (int i = 0; i < 1_000; i++)
        {
            var d = devices[2_000 + i];
            f.Store.Apply(new DeviceChanged { Kind = DeviceChanged.Types.Kind.Updated, Device = TestSupport.Device(d.Id, d.Serial, d.Address, d.Model, DeviceStatus.Unreachable) });
        }

        var changes = watch.Elapsed;
        Assert.Equal(AssistantTexts.OfflineChip(2_000), panel.StartChips[1].Text);
        watch.Restart();
        var text = string.Join(" ", devices.Take(2_000).Select(d => d.Address));
        var links = panel.DeviceIndex!.Find(text);
        var find = watch.Elapsed;
        Assert.Equal(2_000, links.Count);
        output.WriteLine($"open (count 100,000): {open.TotalMilliseconds:F0} ms, 1,000 single changes: {changes.TotalMilliseconds:F0} ms, links in 2,000 words: {find.TotalMilliseconds:F0} ms");
        Assert.True(open < TimeSpan.FromSeconds(3), $"open took {open}");
        Assert.True(changes < TimeSpan.FromMilliseconds(200), $"1,000 changes took {changes}");
        Assert.True(find < TimeSpan.FromMilliseconds(100), $"links took {find}");
    }

    [Fact]
    public async Task DevicesChosenWithTheAtSignGoWithTheMessageAsOneChip()
    {
        var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5), seedSampleData: false);
        using var f = new DevicesFixture(api);
        var devices = Fleet(5, offline: 0);
        f.SeedDevices(devices);
        IReadOnlyList<Guid> initial = [];
        var dialogs = Picker(picker =>
        {
            initial = picker.SelectedIds;
            foreach (var item in picker.Visible.Take(3))
            {
                item.IsChecked = true;
            }

            return true;
        });
        var panel = await PanelAsync(api, f, dialogs: dialogs);

        await panel.ChooseDevicesCommand.ExecuteAsync(null);
        Assert.Equal(3, panel.Mentioned.Count);
        Assert.Equal("3 devices", panel.MentionText);
        await panel.ChooseDevicesCommand.ExecuteAsync(null); // the chip reopens the picker with that choice
        Assert.Equal(3, initial.Count);

        panel.Draft = "Are these up to date?";
        await panel.SendCommand.ExecuteAsync(null);
        Assert.Equal(panel.Items.OfType<AssistantUserItem>().Single().Mentioned, api.AssistantMessages[^1].MentionedDeviceIds);
        Assert.Equal(3, api.AssistantMessages[^1].MentionedDeviceIds.Count);
        Assert.Empty(panel.Mentioned); // they went with the message
        Assert.Equal("3 devices", panel.Items.OfType<AssistantUserItem>().Single().MentionText);

        await panel.RetryCommand.ExecuteAsync(panel.Items[^1]);
        Assert.Equal(3, api.AssistantMessages[^1].MentionedDeviceIds.Count); // Try again sends them again
        panel.EditCommand.Execute(panel.Items.OfType<AssistantUserItem>().Single());
        Assert.Equal(3, panel.Mentioned.Count); // Edit puts them back
        panel.RemoveMentionCommand.Execute(null);
        Assert.False(panel.HasMention);

        // One device: its address; Cancel keeps the choice as it was.
        var one = await PanelAsync(api, f, dialogs: Picker(picker =>
        {
            picker.Visible[0].IsChecked = true;
            return true;
        }));
        await one.ChooseDevicesCommand.ExecuteAsync(null);
        Assert.Equal(f.Store.Find(one.Mentioned[0])!.DisplayAddress, one.MentionText);
        var cancel = await PanelAsync(api, f, dialogs: Picker(_ => false));
        await cancel.ChooseDevicesCommand.ExecuteAsync(null);
        Assert.Empty(cancel.Mentioned);
    }

    [Fact]
    public async Task DevicesInAnAnswerAreLinksWithSelectLiveViewAndWebInterfaceAsTheRoleAllows()
    {
        var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5), seedSampleData: false);
        using var f = new DevicesFixture(api);
        var devices = Fleet(3, offline: 0);
        f.SeedDevices(devices);
        var panel = await PanelAsync(api, f);
        IMarkdownActions actions = panel;

        var links = actions.FindLinks($"Restart {devices[1].Address} and {devices[2].Serial}, not 10.9.9.9.");
        Assert.Equal([devices[1].Id, devices[2].Id], links.Select(l => l.Key));
        Assert.Equal(devices[1].Address, $"Restart {devices[1].Address}".Substring(links[0].Start, links[0].Length));
        Assert.Equal([AssistantTexts.Select, AssistantTexts.OpenLiveView, AssistantTexts.OpenWebInterface], actions.LinkMenu(devices[1].Id).Select(a => a.Text));

        var shown = 0;
        panel.ShowDevicesRequested += (_, _) => shown++;
        actions.OpenLink(devices[1].Id);
        Assert.Equal(1, shown);
        Assert.Equal([devices[1].Id], f.Devices.SelectedDevices.Select(d => d.Id));

        // A role without live view and web interface gets Select only.
        var viewer = Session(admin: false, HostPermissionKeys.AssistantUse);
        var otherApi = new FakeOadmApi(TimeSpan.FromMilliseconds(5), seedSampleData: false);
        using var g = new DevicesFixture(otherApi, session: viewer);
        g.SeedDevices(devices);
        IMarkdownActions limited = await PanelAsync(otherApi, g, viewer);
        Assert.Equal([AssistantTexts.Select], limited.LinkMenu(devices[1].Id).Select(a => a.Text));
    }

    [Fact]
    public async Task TryTheFailedDevicesAgainAsksForANewCardForThemOnly()
    {
        var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5), seedSampleData: false);
        using var f = new DevicesFixture(api);
        var devices = Fleet(3, offline: 0);
        f.SeedDevices(devices);
        var panel = await PanelAsync(api, f);
        f.Select([.. devices.Select(d => d.Id)]);
        panel.Draft = "Restart these";
        await panel.SendCommand.ExecuteAsync(null);
        var preview = panel.Items.OfType<AssistantPreviewItem>().Single();
        Assert.False(preview.CanRetryFailed);
        await panel.RetryFailedCommand.ExecuteAsync(preview); // not run: nothing
        Assert.Empty(api.AssistantRetries);

        await panel.RunPreviewCommand.ExecuteAsync(preview);
        foreach (var task in (await api.ListTasksAsync(CancellationToken.None)).Where(t => preview.TaskIds.Contains(t.Id)))
        {
            var ended = task.Clone();
            ended.State = ended.DeviceId == devices[0].Id ? TaskState.Done : TaskState.Failed;
            ended.Devices[0].Message = ended.DeviceId == devices[1].Id ? "Wrong password" : "The device did not answer.";
            f.Tasks.Apply(new TaskChanged { Kind = TaskChanged.Types.Kind.Added, Task = ended });
        }

        Assert.Equal("1 done, 2 failed: The device did not answer. (1), Wrong password (1)", preview.StatusText);
        Assert.True(preview.CanRetryFailed);

        await panel.RetryFailedCommand.ExecuteAsync(preview);
        var request = Assert.Single(api.AssistantRetries);
        Assert.Equal((panel.ConversationId, preview.Handle), (request.ConversationId, request.Handle));
        Assert.Equal(new[] { devices[1].Id, devices[2].Id }.Order(StringComparer.Ordinal), request.FailedDeviceIds.Order(StringComparer.Ordinal));
        var again = Assert.IsType<AssistantPreviewItem>(panel.Items[^1]);
        Assert.True(again.IsReady); // a new card that still needs Run
        Assert.Equal(2, again.DeviceCount);
        Assert.False(preview.CanRetryFailed);
        Assert.True(panel.HasReadyPreview);
    }

    [Fact]
    public void ARunResultNamesAtMostThreeReasons()
    {
        Assert.Equal("11 done, 1 failed: Wrong password", AssistantTexts.RunResult(11, 1, 0, [("Wrong password", 1)]));
        Assert.Equal("0 done, 1 cancelled, 9 failed: A (4), B (2), C (2), +1 more",
            AssistantTexts.RunResult(0, 9, 1, [("A", 4), ("B", 2), ("C", 2), ("D", 1)]));
    }

    [Fact]
    public async Task TablesCopyAndSaveAsCsvAndCopyChatCopiesTheConversation()
    {
        var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5), seedSampleData: false);
        using var f = new DevicesFixture(api);
        var devices = Fleet(3, offline: 0);
        f.SeedDevices(devices);
        var clipboard = Substitute.For<IClipboardService>();
        clipboard.SetTextAsync(Arg.Any<string>()).Returns(true);
        var dialogs = Substitute.For<IDialogService>();
        var panel = await PanelAsync(api, f, dialogs: dialogs, clipboard: clipboard);
        Assert.False(panel.CopyChatCommand.CanExecute(null)); // nothing to copy yet
        var table = new AssistantTable { Total = 3 };
        table.Rows.AddRange(devices.Take(2).Select(d => new AssistantDeviceRow { DeviceId = d.Id, Name = d.Address, Address = d.Address, Model = d.Model, Status = "OK" }));
        table.AllDeviceIds.AddRange(devices.Select(d => d.Id));
        api.AssistantScript = (_, ct) => Script(
        [
            new AssistantEvent { Tool = new AssistantToolLine { Text = "Looking at devices" } },
            new AssistantEvent { Table = table },
            new AssistantEvent { Text = "| Model | Devices |\n|---|---:|\n| P3265-LVE | 3 |\n" },
        ], ct);
        panel.Draft = "List them";
        await panel.SendCommand.ExecuteAsync(null);
        var item = panel.Items.OfType<AssistantTableItem>().Single();

        var csv = panel.TableCsv(item);
        Assert.Equal(4, csv.Trim().Split('\n').Length); // header + every device of the table, also the one not shown
        Assert.All(devices, d => Assert.Contains(d.Address, csv, StringComparison.Ordinal));
        await panel.CopyTableCsvCommand.ExecuteAsync(item);
        await clipboard.Received(1).SetTextAsync(csv);
        await panel.SaveTableCsvCommand.ExecuteAsync(item);
        await dialogs.Received(1).SaveFileAsync(AssistantTexts.SaveCsv, Arg.Is<string>(n => n.EndsWith(".csv", StringComparison.Ordinal)), Arg.Any<FileType>(), Arg.Any<byte[]>());

        var markdown = MarkdownDocument.Parse(panel.Items.OfType<AssistantAnswerItem>().Single().Text).OfType<MarkdownTable>().Single();
        Assert.Equal("Model,Devices\r\nP3265-LVE,3\r\n", MarkdownDocument.ToCsv(markdown).Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal));

        Assert.True(panel.CopyChatCommand.CanExecute(null));
        var chat = panel.ChatMarkdown();
        Assert.Contains("**You:** List them", chat, StringComparison.Ordinal);
        Assert.Contains("_Looking at devices_", chat, StringComparison.Ordinal);
        Assert.Contains(devices[0].Address, chat, StringComparison.Ordinal);
        Assert.Contains("| P3265-LVE | 3 |", chat, StringComparison.Ordinal);
        Assert.Contains("+1 more", chat, StringComparison.Ordinal);

        // A role without Export devices: no CSV of device tables.
        var viewer = Session(admin: false, HostPermissionKeys.AssistantUse);
        var otherApi = new FakeOadmApi(TimeSpan.FromMilliseconds(5), seedSampleData: false);
        using var g = new DevicesFixture(otherApi, session: viewer);
        Assert.False((await PanelAsync(otherApi, g, viewer)).CanExportTables);
    }

    [Fact]
    public async Task CtrlJOpensThePanelWithTheFocusInTheBoxAndClosesItFromThePanel()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        var panel = await PanelAsync(api);
        panel.Close();
        var focus = 0;
        panel.FocusMessageRequested += (_, _) => focus++;

        Assert.True(panel.ToggleFromKeyboard(focusInPanel: false));
        Assert.True(panel.IsOpen);
        Assert.Equal(1, focus);
        Assert.True(panel.ToggleFromKeyboard(focusInPanel: false)); // open, the focus elsewhere: back into the box
        Assert.True(panel.IsOpen);
        Assert.Equal(2, focus);
        Assert.True(panel.ToggleFromKeyboard(focusInPanel: true));
        Assert.False(panel.IsOpen);
    }

    [Fact]
    public async Task TheEmptyPanelChipsMentionsLinksAndAFailedRunLookRightInDarkAndLight()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5), seedSampleData: false);
            using var f = new DevicesFixture(api);
            var devices = Fleet(1_284, offline: 12);
            f.SeedDevices(devices);
            var dialogs = Picker(picker =>
            {
                foreach (var item in picker.Visible.Take(3))
                {
                    item.IsChecked = true;
                }

                return true;
            });
            var panel = await PanelAsync(api, f, dialogs: dialogs);
            panel.CurrentPageKey = HostPages.Devices;
            var window = PanelWindow(panel);
            window.Show();
            Pump();
            var view = window.GetVisualDescendants().OfType<AssistantPanelView>().Single();
            var texts = VisibleTexts(window);
            Assert.Contains(AssistantTexts.StartHeading, texts);
            Assert.Contains("1,284 devices", texts);
            Assert.Contains("12 offline", texts);
            Assert.Equal(AssistantPanelViewModel.MaxStartCards, view.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("startCard")));
            Save(window, outDir, "assistant-ux2-empty.png"); // the light one: OADM_TEST_APPEARANCE=Light

            // @ opens the picker and stays out of the text; the choice is one chip.
            var box = view.FindControl<TextBox>("MessageBox")!;
            box.Focus();
            Pump();
            window.KeyTextInput("Is ");
            window.KeyTextInput("@");
            await Oadm.Sdk.Testing.Eventually.TrueAsync(() => panel.HasMention, "the picker chose");
            window.KeyTextInput("up to date?");
            Pump();
            Assert.Equal("Is up to date?", panel.Draft);
            Assert.True(view.FindControl<Border>("MentionChip")!.IsEffectivelyVisible);
            Assert.Contains("3 devices", texts = VisibleTexts(window));
            Save(window, outDir, "assistant-ux2-mention.png");

            // An answer with device links and a Markdown table with its CSV buttons.
            api.AssistantScript = (_, ct) => Script(
            [
                new AssistantEvent { Text = $"{devices[0].Address} and {devices[1].Address} run AXIS OS 11.11. {devices[2].Address} is up to date.\n\n| Model | Devices |\n|---|---:|\n| P3265-LVE | 3 |\n" },
            ], ct);
            await panel.SendCommand.ExecuteAsync(null);
            Pump();
            Assert.Contains(view.GetVisualDescendants().OfType<Button>(), b => ToolTip.GetTip(b) as string == AssistantTexts.SaveCsv && b.IsEffectivelyVisible);
            Save(window, outDir, "assistant-ux2-links.png");

            // A run with failures: the result line and Try the failed devices again.
            panel.NewChatCommand.Execute(null);
            f.Select(devices[0].Id, devices[1].Id, devices[2].Id);
            api.AssistantScript = null;
            panel.Draft = "Restart these";
            await panel.SendCommand.ExecuteAsync(null);
            var preview = panel.Items.OfType<AssistantPreviewItem>().Single();
            await panel.RunPreviewCommand.ExecuteAsync(preview);
            foreach (var task in (await api.ListTasksAsync(CancellationToken.None)).Where(t => preview.TaskIds.Contains(t.Id)))
            {
                var ended = task.Clone();
                ended.State = ended.DeviceId == devices[0].Id ? TaskState.Done : TaskState.Failed;
                ended.Devices[0].Message = "Wrong password";
                f.Tasks.Apply(new TaskChanged { Kind = TaskChanged.Types.Kind.Added, Task = ended });
            }

            Pump();
            Assert.True(view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "RetryFailed").IsEffectivelyVisible);
            Assert.Contains("1 done, 2 failed: Wrong password", VisibleTexts(window));
            Save(window, outDir, "assistant-ux2-run-failed.png");
            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }
}
