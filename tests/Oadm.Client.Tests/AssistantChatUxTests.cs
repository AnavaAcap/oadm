using System.Runtime.CompilerServices;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;

using Grpc.Core;

using Oadm.Client.Api;
using Oadm.Client.Assistant;
using Oadm.Contracts.V1;

using static Oadm.Client.Tests.AssistantTests;

namespace Oadm.Client.Tests;

/// <summary>
/// The Assistant panel like the chat apps (docs/spec/assistant.md "The panel"): Thinking until the answer's first event,
/// following the end only while the user is there, Try again and Edit (replace_last, the old entries gone in one step),
/// files pasted or dropped, Escape and Up arrow, and the headless screenshots of the composer.
/// </summary>
public sealed class AssistantChatUxTests
{
    private static async IAsyncEnumerable<AssistantEvent> Gated(TaskCompletionSource gate, IEnumerable<AssistantEvent> before, IEnumerable<AssistantEvent> after,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var e in before)
        {
            await Task.Yield();
            yield return e;
        }

        await gate.Task.WaitAsync(ct);
        foreach (var e in after)
        {
            await Task.Yield();
            yield return e;
        }
    }

    private static AssistantEvent Text(string text) => new() { Text = text };

    private static AssistantEvent Done(string error = "", bool stopped = false) => new() { Done = new AssistantDone { Error = error, Stopped = stopped } };

    [Fact]
    public async Task ThinkingShowsFromSendBelowTheNoticeUntilTheAnswersFirstEvent()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        api.AssistantScript = (_, ct) => Gated(gate, [new AssistantEvent { Notice = "Your messages go to Anthropic." }], [Text("Hello."), Done()], ct);
        var panel = await PanelAsync(api);
        panel.Draft = "Hi";
        var sending = panel.SendCommand.ExecuteAsync(null);
        await Oadm.Sdk.Testing.Eventually.TrueAsync(() => panel.Items.OfType<AssistantNoticeItem>().Any(), "the notice arrived");

        Assert.True(panel.IsThinking);
        Assert.Equal([typeof(AssistantUserItem), typeof(AssistantNoticeItem), typeof(AssistantThinkingItem)], panel.Items.Select(i => i.GetType()));

        gate.SetResult();
        await sending.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(panel.IsThinking);
        Assert.Equal([typeof(AssistantUserItem), typeof(AssistantNoticeItem), typeof(AssistantAnswerItem)], panel.Items.Select(i => i.GetType()));
    }

    [Fact]
    public async Task ThinkingGoesWithAnErrorOrARefusal()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        api.AssistantScript = (_, ct) => Script([Done("The provider did not answer. Try again.")], ct);
        var panel = await PanelAsync(api);
        panel.Draft = "Hi";
        await panel.SendCommand.ExecuteAsync(null);
        Assert.False(panel.IsThinking);
        Assert.DoesNotContain(panel.Items, i => i is AssistantThinkingItem);

        api.AssistantScript = (_, _) => Refused();
        panel.Draft = "Again";
        await panel.SendCommand.ExecuteAsync(null);
        Assert.False(panel.IsThinking);
        Assert.DoesNotContain(panel.Items, i => i is AssistantThinkingItem);
    }

    private static async IAsyncEnumerable<AssistantEvent> Refused()
    {
        await Task.Yield();
        throw new RpcException(new Status(StatusCode.ResourceExhausted, "The assistant reached today's limit. It works again tomorrow."));
#pragma warning disable CS0162 // an iterator needs a yield
        yield break;
#pragma warning restore CS0162
    }

    [Fact]
    public async Task TryAgainSendsTheLastMessageAgainInPlaceOfItsAnswer()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        var answers = 0;
        api.AssistantScript = (_, ct) => Script([Text($"Answer {++answers}."), Done()], ct);
        var panel = await PanelAsync(api);
        panel.Draft = "First";
        await panel.SendCommand.ExecuteAsync(null);
        panel.Draft = "Second";
        await panel.SendCommand.ExecuteAsync(null);

        var last = (AssistantAnswerItem)panel.Items[^1];
        Assert.True(last.ShowsRetry);
        Assert.Single(panel.Items, i => i.ShowsRetry); // only under the last answer
        Assert.True(((AssistantUserItem)panel.Items[2]).CanEdit);
        Assert.False(((AssistantUserItem)panel.Items[0]).CanEdit);

        var changes = 0;
        panel.Items.CollectionChanged += (_, e) => changes += e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset ? 1 : 0;
        await panel.RetryCommand.ExecuteAsync(last);

        var sent = api.AssistantMessages[^1];
        Assert.Equal(("Second", true), (sent.Text, sent.ReplaceLast));
        Assert.False(api.AssistantMessages[1].ReplaceLast);
        Assert.Equal(1, changes); // the old exchange left in one step
        Assert.Equal(["First", "Answer 1.", "Second", "Answer 3."], panel.Items.Select(i => i switch
        {
            AssistantUserItem u => u.Text,
            AssistantAnswerItem a => a.Text,
            _ => "?",
        }));
    }

    [Fact]
    public async Task TryAgainShowsOnAnErrorAndAStopAndAMessageTheServerNeverHadIsSentAsANewOne()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        api.AssistantScript = (_, ct) => Script([Text("Half"), Done(stopped: true)], ct);
        var panel = await PanelAsync(api);
        panel.Draft = "Tell me";
        await panel.SendCommand.ExecuteAsync(null);
        var stopped = Assert.IsType<AssistantNoticeItem>(panel.Items[^1]);
        Assert.True(stopped.ShowsRetry);
        Assert.False(panel.Items.OfType<AssistantAnswerItem>().Single().ShowsRetry);

        api.AssistantScript = (_, _) => Refused();
        await panel.RetryCommand.ExecuteAsync(stopped);
        Assert.True(api.AssistantMessages[^1].ReplaceLast); // the stopped exchange was on the server
        var refusal = Assert.IsType<AssistantNoticeItem>(panel.Items[^1]);
        Assert.True(refusal.IsError && refusal.ShowsRetry);

        api.AssistantScript = (_, ct) => Script([Text("Done."), Done()], ct);
        await panel.RetryCommand.ExecuteAsync(refusal);
        Assert.False(api.AssistantMessages[^1].ReplaceLast); // the refused one never reached the conversation
        Assert.Equal([typeof(AssistantUserItem), typeof(AssistantAnswerItem)], panel.Items.Select(i => i.GetType()));
    }

    [Fact]
    public async Task NothingToTryAgainOnTheServerSendsTheMessageAsANewOne()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        api.AssistantScript = (m, ct) => m.ReplaceLast ? NothingToRetry() : Script([Text("Yes."), Done()], ct);
        var panel = await PanelAsync(api);
        panel.Draft = "Hi";
        await panel.SendCommand.ExecuteAsync(null);
        await panel.RetryCommand.ExecuteAsync(panel.Items[^1]);

        Assert.Equal([false, true, false], api.AssistantMessages.Select(m => m.ReplaceLast));
        Assert.Equal([typeof(AssistantUserItem), typeof(AssistantAnswerItem)], panel.Items.Select(i => i.GetType()));

        static async IAsyncEnumerable<AssistantEvent> NothingToRetry()
        {
            await Task.Yield();
            throw new RpcException(new Status(StatusCode.FailedPrecondition, AssistantTexts.NothingToRetry));
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    [Fact]
    public async Task EditPutsTheLastMessageBackAndSendingReplacesThatExchange()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oadm-edit-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, new byte[100]);
        try
        {
            using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5), seedSampleData: false);
            api.AssistantScript = (m, ct) => Script([Text("About " + m.Text), Done()], ct);
            var panel = await PanelAsync(api);
            await panel.AttachAsync(path);
            panel.Draft = "the lobby";
            await panel.SendCommand.ExecuteAsync(null);
            var user = (AssistantUserItem)panel.Items[0];
            var focus = 0;
            panel.FocusMessageRequested += (_, _) => focus++;

            panel.EditCommand.Execute(user);
            Assert.Equal("the lobby", panel.Draft);
            Assert.Single(panel.Attachments); // its files come back too
            Assert.Equal(1, focus);

            panel.Draft = "the yard";
            await panel.SendCommand.ExecuteAsync(null);
            var sent = api.AssistantMessages[^1];
            Assert.Equal(("the yard", true, 1), (sent.Text, sent.ReplaceLast, sent.Attachments.Count));
            Assert.Equal(["the yard", "About the yard"], panel.Items.Select(i => i is AssistantUserItem u ? u.Text : ((AssistantAnswerItem)i).Text));

            // An emptied box drops the edit: the next message is a new one.
            panel.EditCommand.Execute(panel.Items[0]);
            panel.Draft = string.Empty;
            panel.Draft = "the gate";
            await panel.SendCommand.ExecuteAsync(null);
            Assert.False(api.AssistantMessages[^1].ReplaceLast);
            Assert.Equal(4, panel.Items.Count);

            // Up arrow: only in an empty box, and only the last message.
            panel.Draft = "typing";
            Assert.False(panel.EditLast());
            panel.Draft = string.Empty;
            Assert.True(panel.EditLast());
            Assert.Equal("the gate", panel.Draft);
            panel.EditCommand.Execute(panel.Items[0]); // not the last one: nothing
            Assert.Equal("the gate", panel.Draft);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ScrolledUpTheListStopsFollowingAndGoToTheLatestFollowsAgain()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        api.AssistantScript = (_, ct) => Script([Text("Yes."), Done()], ct);
        var panel = await PanelAsync(api);
        Assert.False(panel.ShowJumpToLatest); // empty
        panel.IsFollowing = false;
        Assert.False(panel.ShowJumpToLatest);

        panel.Draft = "Hi";
        await panel.SendCommand.ExecuteAsync(null);
        Assert.True(panel.IsFollowing); // a new message goes to the end
        panel.IsFollowing = false; // the user scrolled up
        Assert.True(panel.ShowJumpToLatest);

        var scrolls = 0;
        panel.ScrollToEndRequested += (_, _) => scrolls++;
        panel.JumpToLatestCommand.Execute(null);
        Assert.True(panel.IsFollowing);
        Assert.False(panel.ShowJumpToLatest);
        Assert.Equal(1, scrolls);
    }

    [Fact]
    public async Task DroppedOrPastedFilesAttachLikeAttachFileWithItsLimitAndAPastedPictureBecomesAPng()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"oadm-drop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var files = Enumerable.Range(1, 11).Select(i => Path.Combine(folder, $"file{i}.bin")).ToList();
            foreach (var file in files)
            {
                await File.WriteAllBytesAsync(file, new byte[10]);
            }

            using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5), seedSampleData: false);
            var panel = await PanelAsync(api);
            await panel.AttachFilesAsync([files[0], folder, files[1]]); // a folder is skipped
            Assert.Equal(["file1.bin", "file2.bin"], panel.Attachments.Select(a => a.Name));

            await panel.AttachPastedImageAsync(Jpeg(), CancellationToken.None);
            await panel.AttachPastedImageAsync(Jpeg(), CancellationToken.None);
            Assert.Equal(["Pasted image 1.png", "Pasted image 2.png"], panel.Attachments.Skip(2).Select(a => a.Name));
            Assert.All(Directory.GetDirectories(Path.GetTempPath(), "oadm-paste-*"), d => Assert.Empty(Directory.GetFiles(d, "Pasted image *")));

            await panel.AttachFilesAsync(files.Skip(2));
            Assert.Equal(AssistantTexts.MaxAttachments, panel.Attachments.Count);
            Assert.Equal((AssistantTexts.TooManyFiles, true), (panel.Message, panel.MessageIsError));
            await panel.AttachPastedImageAsync(Jpeg(), CancellationToken.None);
            Assert.Equal(AssistantTexts.MaxAttachments, panel.Attachments.Count);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task EscapeStopsTheAnswerAndUpArrowInTheEmptyBoxEditsTheLastMessage()
    {
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            api.AssistantScript = (_, ct) => Gated(gate, [Text("Let me look")], [Done()], ct);
            var panel = await PanelAsync(api);
            var window = PanelWindow(panel);
            window.Show();
            Pump();
            var view = window.GetVisualDescendants().OfType<AssistantPanelView>().Single();
            var box = view.FindControl<TextBox>("MessageBox")!;
            box.Focus();
            panel.Draft = "Tell me everything";
            var sending = panel.SendCommand.ExecuteAsync(null);
            await Oadm.Sdk.Testing.Eventually.TrueAsync(() => panel.Items.OfType<AssistantAnswerItem>().Any(), "the answer began");
            Pump();

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            await sending.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal([panel.ConversationId!], api.AssistantStops);

            box.Focus();
            Pump();
            window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
            Pump();
            Assert.Equal("Tell me everything", panel.Draft);
            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task TheComposerThinkingAnswerScrollStreamingDropAndAttachmentsLookLikeTheChatApps()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var folder = Path.Combine(Path.GetTempPath(), $"oadm-shots-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var done = await HeadlessSession.Shared.Dispatch(async () =>
            {
                var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5), seedSampleData: false);
                using var f = new DevicesFixture(api);
                var devices = AssistantTests.Devices(5);
                f.SeedDevices(devices);
                var panel = await PanelAsync(api, f);
                var window = PanelWindow(panel);
                window.Show();
                Pump();
                var view = window.GetVisualDescendants().OfType<AssistantPanelView>().Single();
                var send = view.FindControl<Button>("Send")!;
                Assert.Contains("send", send.Classes);
                Assert.False(send.IsEffectivelyEnabled); // nothing written: dimmed
                Assert.Equal(AssistantTexts.EnterMessage, ToolTip.GetTip(send));
                Assert.Equal(AssistantTexts.AttachFile, ToolTip.GetTip(view.FindControl<Button>("AttachFile")!));
                Save(window, outDir, "assistant-ux-empty.png");

                // Thinking until the first event.
                f.Select(devices[0].Id, devices[1].Id, devices[2].Id);
                var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                api.AssistantScript = (_, ct) => Gated(gate, [], [Text("**3 devices** are selected. Two of them run AXIS OS 11.11, one runs 12.6.97.\n\n- 10.0.0.1\n- 10.0.0.2\n- 10.0.0.3"), Done()], ct);
                panel.Draft = "What runs on the selected cameras?";
                var sending = panel.SendCommand.ExecuteAsync(null);
                Pump();
                Assert.Contains(AssistantTexts.Thinking, VisibleTexts(window));
                Assert.True(view.FindControl<Button>("Stop")!.IsEffectivelyVisible);
                Save(window, outDir, "assistant-ux-thinking.png");
                gate.SetResult();
                await sending;
                Pump();
                Assert.DoesNotContain(AssistantTexts.Thinking, VisibleTexts(window));
                Assert.Contains(view.GetVisualDescendants().OfType<Button>(), b => b.Name == "RetryAnswer" && b.IsEffectivelyVisible);
                Assert.Contains("3 devices selected", VisibleTexts(window));
                Save(window, outDir, "assistant-ux-answer.png");

                // Streaming: Stop in place of the send arrow (a new chat: its first piece shows at once).
                panel.NewChatCommand.Execute(null);
                var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                api.AssistantScript = (_, ct) => Gated(hold, [Text("Checking the firmware of the three cameras. ")], [Done()], ct);
                panel.Draft = "And are they up to date?";
                sending = panel.SendCommand.ExecuteAsync(null);
                await Oadm.Sdk.Testing.Eventually.TrueAsync(() => panel.Items.OfType<AssistantAnswerItem>().Any(a => a.Text.Length > 0), "the answer began");
                Pump();
                Assert.True(view.FindControl<Button>("Stop")!.IsEffectivelyVisible);
                Assert.False(send.IsEffectivelyVisible);
                Assert.Equal(AssistantTexts.Stop, ToolTip.GetTip(view.FindControl<Button>("Stop")!));
                Save(window, outDir, "assistant-ux-streaming.png");
                hold.SetResult();
                await sending;

                // Scrolled up: Go to the latest; new content leaves the place alone.
                api.AssistantScript = (_, ct) => Script([Text(string.Join("\n\n", Enumerable.Range(1, 40).Select(i => $"Paragraph {i} of a long answer about the cameras and their firmware."))), Done()], ct);
                panel.Draft = "Tell me more";
                await panel.SendCommand.ExecuteAsync(null);
                Pump();
                var scroller = view.FindControl<ListBox>("Conversation")!.GetVisualDescendants().OfType<ScrollViewer>().First();
                Assert.True(scroller.Extent.Height > scroller.Viewport.Height + 200, "the answer is longer than the panel");
                Assert.True(scroller.Offset.Y >= scroller.Extent.Height - scroller.Viewport.Height - AssistantPanelView.FollowDistance);
                Assert.True(panel.IsFollowing);
                scroller.Offset = new Avalonia.Vector(0, 0);
                Pump();
                Assert.False(panel.IsFollowing);
                Assert.True(view.FindControl<Button>("JumpToLatest")!.IsEffectivelyVisible);
                Save(window, outDir, "assistant-ux-scrolled-up.png");
                panel.JumpToLatestCommand.Execute(null);
                Pump();
                Assert.True(scroller.Offset.Y >= scroller.Extent.Height - scroller.Viewport.Height - AssistantPanelView.FollowDistance);
                Assert.False(view.FindControl<Button>("JumpToLatest")!.IsEffectivelyVisible);

                // Dragging files over the panel.
                panel.IsDragOver = true;
                Pump();
                Assert.Contains(AssistantTexts.DropFiles, VisibleTexts(window));
                Save(window, outDir, "assistant-ux-drop.png");
                panel.IsDragOver = false;

                // Chips inside the box.
                var first = Path.Combine(folder, "AXIS_OS_12.6.97.bin");
                var second = Path.Combine(folder, "lobby-config.txt");
                await File.WriteAllBytesAsync(first, new byte[48_000]);
                await File.WriteAllBytesAsync(second, new byte[900]);
                await panel.AttachFilesAsync([first, second]);
                await panel.AttachPastedImageAsync(Jpeg(), CancellationToken.None);
                panel.Draft = "Upgrade the selected cameras with this file";
                Pump();
                var composer = view.FindControl<Border>("Composer")!;
                Assert.All(view.FindControl<ItemsControl>("AttachmentList")!.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("chatChip")),
                    chip => Assert.Contains(composer, chip.GetVisualAncestors()));
                Assert.True(scroller.Offset.Y >= scroller.Extent.Height - scroller.Viewport.Height - 1, "the grown box keeps the end in view");
                Save(window, outDir, "assistant-ux-attachments.png");
                window.Close();
                return true;
            }, CancellationToken.None);
            Assert.True(done);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
