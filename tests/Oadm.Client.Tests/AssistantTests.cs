using System.Runtime.CompilerServices;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Google.Protobuf;

using Grpc.Core;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Assistant;
using Oadm.Client.Controls;
using Oadm.Client.Dialogs;
using Oadm.Client.Settings;
using Oadm.Client.Shell;
using Oadm.Contracts.V1;
using Oadm.Sdk.Plugins;

using UserRole = Oadm.Contracts.V1.UserRole;

namespace Oadm.Client.Tests;

/// <summary>
/// The Assistant in the client (docs/spec/assistant.md): the panel's view model against the scripted fake AssistantService
/// (streaming items, the selection and files of a message, Run with the handle, Stop, New chat, the title bar button), the
/// Settings section with its validation below the fields and the server's refusals on their field, the Markdown subset,
/// and headless screenshots in OADM_SCREENSHOT_DIR.
/// </summary>
public sealed class AssistantTests
{
    private static UserSession Session(bool admin, params string[] permissions)
    {
        var session = new UserSession();
        var user = new UserInfo
        {
            UserName = admin ? "admin" : "tech",
            Role = admin ? UserRole.Admin : UserRole.Operator,
            RoleId = admin ? "00000000-0000-0000-0000-000000000001" : "00000000-0000-0000-0000-000000000002",
            RoleName = admin ? "Administrator" : "Operator",
            AllPermissions = admin,
        };
        user.Permissions.AddRange(permissions);
        session.SignIn(user, "fake");
        return session;
    }

    private static AssistantPanelViewModel Panel(FakeOadmApi api, DevicesFixture? f = null, UserSession? session = null, IDialogService? dialogs = null,
        IClipboardService? clipboard = null)
    {
        var availability = new AssistantAvailability();
        availability.Apply(new AssistantSettingsState { Settings = new AssistantSettings { Enabled = true, Provider = "anthropic" } });
        var panel = new AssistantPanelViewModel(api, availability, session ?? Session(admin: true), f?.Devices, f?.Tasks, dialogs, clipboard);
        panel.Open();
        return panel;
    }

    private static async IAsyncEnumerable<AssistantEvent> Script(IEnumerable<AssistantEvent> events, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var e in events)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return e;
        }
    }

    private static AssistantTable Table(int total, params Device[] devices)
    {
        var table = new AssistantTable { Total = total };
        table.Rows.AddRange(devices.Select(d => new AssistantDeviceRow { DeviceId = d.Id, Name = d.Address, Address = d.Address, Model = d.Model, Status = "Unreachable" }));
        table.AllDeviceIds.AddRange(devices.Select(d => d.Id));
        return table;
    }

    private static Device[] Devices(int count) =>
        [.. Enumerable.Range(1, count).Select(i => TestSupport.Device(Guid.NewGuid().ToString(), $"ACCC8E{i:X6}", $"10.0.{i / 250}.{i % 250 + 1}", i % 2 == 0 ? "P3265-LVE" : "M3086-V"))];

    private static byte[] Jpeg() => FakeOadmApi.TestPicture(320, 180, "10.0.0.48", "P3265-LVE", DateTimeOffset.UnixEpoch, 7);

    [Fact]
    public async Task StreamingEventsBuildTextToolLinesTablesPicturesCardsAndErrors()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        var devices = Devices(2);
        var card = new AssistantPreviewCard { Handle = "h1", Title = "Restart", DeviceCount = 25, Skipped = { "3 devices: Site offline" }, Warnings = { "The devices restart." } };
        card.FirstDevices.AddRange(["10.0.0.1", "10.0.0.2"]);
        api.AssistantScript = (_, ct) => Script(
        [
            new AssistantEvent { Notice = "Your messages and the data the assistant reads go to Anthropic." },
            new AssistantEvent { Text = "Two devices " },
            new AssistantEvent { Text = "are **offline**." },
            new AssistantEvent { Tool = new AssistantToolLine { Text = "Looking at devices" } },
            new AssistantEvent { Table = Table(60, devices) },
            new AssistantEvent { Picture = new AssistantPicture { DeviceId = devices[0].Id, DeviceName = "10.0.0.1", Jpeg = ByteString.CopyFrom(Jpeg()) } },
            new AssistantEvent { Preview = card },
            new AssistantEvent { Tool = new AssistantToolLine { Text = "Not allowed", Failed = true } },
            new AssistantEvent { Notice = "Your messages and the data the assistant reads go to Anthropic." }, // once only
            new AssistantEvent { Done = new AssistantDone { Error = "The provider did not answer. Try again." } },
        ], ct);
        var panel = Panel(api);
        Assert.True(panel.IsEmpty);

        panel.Draft = "Which devices are offline?";
        await panel.SendCommand.ExecuteAsync(null);

        Assert.False(panel.IsStreaming);
        Assert.Equal(
            [typeof(AssistantUserItem), typeof(AssistantNoticeItem), typeof(AssistantAnswerItem), typeof(AssistantToolItem), typeof(AssistantTableItem),
                typeof(AssistantPictureItem), typeof(AssistantPreviewItem), typeof(AssistantToolItem), typeof(AssistantNoticeItem)],
            panel.Items.Select(i => i.GetType()));
        Assert.Equal("Two devices are **offline**.", ((AssistantAnswerItem)panel.Items[2]).Text); // the pieces join into one answer
        var table = (AssistantTableItem)panel.Items[4];
        Assert.True(table.HasMore);
        Assert.Equal("+58 more", table.MoreText);
        var preview = (AssistantPreviewItem)panel.Items[6];
        Assert.Equal(("Restart", "25 devices", "10.0.0.1, 10.0.0.2, +23 more"), (preview.Title, preview.CountText, preview.DevicesText));
        Assert.True(preview.IsReady);
        Assert.True(panel.HasReadyPreview); // Run is the main button now
        Assert.True(((AssistantToolItem)panel.Items[7]).Failed);
        var error = (AssistantNoticeItem)panel.Items[8];
        Assert.True(error.IsError);
        Assert.Equal("The provider did not answer. Try again.", error.Text);
        Assert.Equal(string.Empty, panel.Draft);
    }

    [Fact]
    public async Task AMessageCarriesTheGridSelectionAndTheAttachedFiles()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oadm-assistant-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, new byte[2048]);
        try
        {
            var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5), seedSampleData: false);
            using var f = new DevicesFixture(api);
            var devices = Devices(3);
            f.SeedDevices(devices);
            var panel = Panel(api, f);
            Assert.Equal(string.Empty, panel.SelectionText);
            Assert.False(panel.HasSelection);

            f.Select(devices[0].Id, devices[2].Id);
            Assert.Equal("2 devices selected", panel.SelectionText);

            await panel.AttachAsync(path);
            await panel.AttachAsync(path);
            Assert.Equal(2, panel.Attachments.Count);
            panel.RemoveAttachmentCommand.Execute(panel.Attachments[1]);
            var file = Assert.Single(panel.Attachments);
            Assert.Equal("2 KB", file.SizeText.Replace(".0", string.Empty, StringComparison.Ordinal));

            panel.Draft = "Upgrade these cameras";
            await panel.SendCommand.ExecuteAsync(null);
            var sent = Assert.Single(api.AssistantMessages);
            Assert.Equal(panel.ConversationId, sent.ConversationId);
            Assert.Equal("Upgrade these cameras", sent.Text);
            Assert.Equal([devices[0].Id, devices[2].Id], sent.SelectedDeviceIds);
            var attachment = Assert.Single(sent.Attachments);
            Assert.Equal((file.FileId, file.Name, 2048L), (attachment.FileId, attachment.Name, attachment.Size));
            Assert.Empty(panel.Attachments); // they went with the message
            Assert.True(((AssistantUserItem)panel.Items[0]).HasFiles);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MoreThanTenThousandSelectedDevicesStopSendWithTheMessageBelowTheBox()
    {
        var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5), seedSampleData: false);
        using var f = new DevicesFixture(api);
        var devices = Devices(AssistantTexts.MaxSelection + 1);
        f.SeedDevices(devices);
        var panel = Panel(api, f);
        panel.Draft = "Which of these are offline?";
        f.Devices.SelectedDevices.ReplaceAll(f.Store.Devices);
        Assert.Equal(AssistantTexts.TooManySelected, panel.SelectionError);
        Assert.False(panel.SendCommand.CanExecute(null));
        Assert.Equal(AssistantTexts.TooManySelected, panel.SendTip);

        f.Devices.SelectedDevices.RemoveAt(0); // 10,000: fine
        Assert.Null(panel.SelectionError);
        Assert.True(panel.SendCommand.CanExecute(null));
        Assert.Equal("10,000 devices selected", panel.SelectionText.Replace(' ', ',').Replace('.', ','));
    }

    [Fact]
    public async Task RunCallsTheServiceWithTheHandleAndTheCardFollowsItsTasks()
    {
        var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5), seedSampleData: false);
        using var f = new DevicesFixture(api);
        var devices = Devices(3);
        f.SeedDevices(devices);
        var panel = Panel(api, f);
        f.Select([.. devices.Select(d => d.Id)]);
        panel.Draft = "Restart these";
        await panel.SendCommand.ExecuteAsync(null);
        var preview = Assert.Single(panel.Items.OfType<AssistantPreviewItem>());
        Assert.Equal(3, preview.DeviceCount);
        Assert.Empty(api.AssistantRuns); // nothing runs without the click

        await panel.RunPreviewCommand.ExecuteAsync(preview);
        var run = Assert.Single(api.AssistantRuns);
        Assert.Equal((panel.ConversationId, preview.Handle), (run.ConversationId, run.Handle));
        Assert.Equal(PreviewState.Started, preview.State);
        Assert.Equal(3, preview.TaskIds.Count);
        Assert.True(preview.IsActive);
        Assert.False(panel.HasReadyPreview);

        // The task stream reaches the client's task mirror: the card follows.
        var tasks = await api.ListTasksAsync(CancellationToken.None);
        foreach (var task in tasks.Where(t => preview.TaskIds.Contains(t.Id)))
        {
            var ended = task.Clone();
            ended.State = ended.DeviceId == devices[1].Id ? TaskState.Failed : TaskState.Done;
            f.Tasks.Apply(new TaskChanged { Kind = TaskChanged.Types.Kind.Added, Task = ended });
        }

        Assert.False(preview.IsActive);
        Assert.True(preview.HasFailed);
        Assert.Equal("Done on 2 devices, failed on 1 device", preview.StatusText);

        await panel.RunPreviewCommand.ExecuteAsync(preview); // used: no second run
        Assert.Single(api.AssistantRuns);
    }

    [Fact]
    public async Task ARunOfAPreviewThatEndedSaysWhyOnTheCard()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        api.AssistantScript = (_, ct) => Script([new AssistantEvent { Preview = new AssistantPreviewCard { Handle = "gone", Title = "Restart", DeviceCount = 1 } }], ct);
        var panel = Panel(api);
        panel.Draft = "Restart it";
        await panel.SendCommand.ExecuteAsync(null);
        var preview = panel.Items.OfType<AssistantPreviewItem>().Single();
        await panel.RunPreviewCommand.ExecuteAsync(preview);
        Assert.Equal(PreviewState.Refused, preview.State);
        Assert.Equal(FakeOadmApi.AssistantPreviewEnded, preview.StatusText);

        var other = new AssistantPreviewItem(new AssistantPreviewCard { Handle = "x", Title = "Restart", DeviceCount = 1 });
        panel.CancelPreviewCommand.Execute(other);
        Assert.Equal(PreviewState.Cancelled, other.State);
    }

    [Fact]
    public async Task StopEndsTheStreamingAnswerAndTheConversationStays()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        api.AssistantScript = Endless;
        var panel = Panel(api);
        var conversation = panel.ConversationId;
        panel.Draft = "Tell me everything";
        var sending = panel.SendCommand.ExecuteAsync(null);
        await TestSupport.WaitUntilAsync(() => panel.Items.OfType<AssistantAnswerItem>().Any());
        Assert.True(panel.IsStreaming);
        Assert.True(panel.StopCommand.CanExecute(null));
        Assert.False(panel.SendCommand.CanExecute(null));

        await panel.StopCommand.ExecuteAsync(null);
        await sending.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal([conversation!], api.AssistantStops);
        Assert.False(panel.IsStreaming);
        Assert.Equal(AssistantTexts.Stopped, Assert.IsType<AssistantNoticeItem>(panel.Items[^1]).Text);
        Assert.Equal(conversation, panel.ConversationId);
        Assert.Empty(api.AssistantEnds);

        static async IAsyncEnumerable<AssistantEvent> Endless(AssistantMessage message, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new AssistantEvent { Text = "Let me look" };
            await Task.Delay(Timeout.Infinite, ct);
        }
    }

    [Fact]
    public async Task NewChatAndCloseEndTheConversationAndForgetIt()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        var panel = Panel(api);
        var first = panel.ConversationId!;
        Assert.True(Guid.TryParse(first, out _));
        await panel.SendSuggestionCommand.ExecuteAsync(AssistantTexts.Suggestions[0]);
        Assert.Equal(AssistantTexts.Suggestions[0], api.AssistantMessages.Single().Text);
        Assert.NotEmpty(panel.Items);

        panel.NewChatCommand.Execute(null);
        Assert.Empty(panel.Items);
        Assert.True(panel.IsEmpty);
        var second = panel.ConversationId!;
        Assert.NotEqual(first, second);
        await TestSupport.WaitUntilAsync(() => api.AssistantEnds.Contains(first));

        panel.CloseCommand.Execute(null);
        Assert.False(panel.IsOpen);
        Assert.Null(panel.ConversationId);
        await TestSupport.WaitUntilAsync(() => api.AssistantEnds.Contains(second));
        panel.ToggleCommand.Execute(null);
        Assert.True(panel.IsOpen);
        Assert.NotEqual(second, panel.ConversationId);
    }

    [Fact]
    public async Task SignOutClosesThePanelAndEndsTheConversation()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        var session = Session(admin: true);
        var panel = Panel(api, session: session);
        var conversation = panel.ConversationId!;
        session.SignOut();
        Assert.False(panel.IsOpen);
        Assert.False(panel.IsAvailable);
        await TestSupport.WaitUntilAsync(() => api.AssistantEnds.Contains(conversation));
    }

    [Fact]
    public async Task TheTitleBarButtonShowsOnlyWhileTheAssistantIsOnAndTheRoleMayUseIt()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        var availability = new AssistantAvailability();
        var admin = new AssistantPanelViewModel(api, availability, Session(admin: true));
        var withPermission = new AssistantPanelViewModel(api, availability, Session(admin: false, HostPermissionKeys.AssistantUse));
        var without = new AssistantPanelViewModel(api, availability, Session(admin: false, HostPermissionKeys.DevicesLiveView));
        Assert.False(admin.IsAvailable); // not read yet

        await admin.RefreshAsync(CancellationToken.None); // the fake starts on
        Assert.True(admin.IsAvailable);
        Assert.True(withPermission.IsAvailable);
        Assert.False(without.IsAvailable);
        without.ToggleCommand.Execute(null);
        Assert.False(without.IsOpen);

        admin.Open();
        var settings = (await api.GetAssistantSettingsAsync(CancellationToken.None)).Settings;
        settings.Enabled = false;
        availability.Apply(await api.SetAssistantSettingsAsync(settings, CancellationToken.None));
        Assert.False(admin.IsAvailable); // the button hides
        Assert.True(admin.IsOpen); // an open panel stays readable until its X

        var old = Substitute.For<IOadmApi>(); // a server without the assistant
        old.GetAssistantSettingsAsync(Arg.Any<CancellationToken>()).Returns<AssistantSettingsState>(_ => throw new RpcException(new Status(StatusCode.Unimplemented, "")));
        var oldPanel = new AssistantPanelViewModel(old, new AssistantAvailability(), Session(admin: true));
        await oldPanel.RefreshAsync(CancellationToken.None);
        Assert.False(oldPanel.IsAvailable);
    }

    [Fact]
    public async Task ATableRowAndSelectAllSelectTheDevicesInTheGridAndShowTheDevicesPage()
    {
        var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5), seedSampleData: false);
        using var f = new DevicesFixture(api);
        var devices = Devices(5);
        f.SeedDevices(devices);
        api.AssistantScript = (_, ct) => Script([new AssistantEvent { Table = Table(5, devices) }], ct);
        var panel = Panel(api, f);
        var shown = 0;
        panel.ShowDevicesRequested += (_, _) => shown++;
        await panel.SendSuggestionCommand.ExecuteAsync(AssistantTexts.Suggestions[0]);
        var table = panel.Items.OfType<AssistantTableItem>().Single();

        f.Devices.SearchText = "10.0.0.1"; // a search that hides the others is cleared
        table.SelectedRow = table.Rows[3];
        Assert.Equal([devices[3].Id], f.Devices.SelectedDevices.Select(d => d.Id));
        Assert.Equal(string.Empty, f.Devices.SearchText);
        panel.SelectAllCommand.Execute(table);
        Assert.Equal(devices.Select(d => d.Id).Order(), f.Devices.SelectedDevices.Select(d => d.Id).Order());
        Assert.Equal(2, shown);
    }

    [Fact]
    public async Task CopyPutsTheAnswerOnTheClipboard()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        api.AssistantScript = (_, ct) => Script([new AssistantEvent { Text = "**3** devices" }], ct);
        var clipboard = Substitute.For<IClipboardService>();
        clipboard.SetTextAsync(Arg.Any<string>()).Returns(true);
        var panel = Panel(api, clipboard: clipboard);
        await panel.SendSuggestionCommand.ExecuteAsync("How many?");
        await panel.CopyCommand.ExecuteAsync(panel.Items.OfType<AssistantAnswerItem>().Single());
        await clipboard.Received(1).SetTextAsync("**3** devices");
        Assert.Equal(AssistantTexts.Copied, panel.Message);
    }

    [Fact]
    public async Task ARefusalOfChatShowsInTheConversation()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        var panel = Panel(api);
        var settings = (await api.GetAssistantSettingsAsync(CancellationToken.None)).Settings;
        settings.Enabled = false;
        await api.SetAssistantSettingsAsync(settings, CancellationToken.None);
        await panel.SendSuggestionCommand.ExecuteAsync("Hello");
        var error = Assert.IsType<AssistantNoticeItem>(panel.Items[^1]);
        Assert.True(error.IsError);
        Assert.Equal(FakeOadmApi.AssistantOff, error.Text);
    }

    // ------------------------------------------------------------ settings

    [Fact]
    public async Task TheSettingsSectionValidatesBelowTheFieldsAndSavesForAdministrators()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        var availability = new AssistantAvailability();
        var form = new AssistantSettingsViewModel(api, Session(admin: true), availability);
        await form.LoadAsync();
        Assert.True(form.IsAvailable);
        Assert.True(availability.IsOn);
        Assert.Equal(AssistantProvider.Anthropic, form.Provider);
        Assert.Equal("claude-opus-5-5", form.Model);
        Assert.Equal(AssistantTexts.Saved, form.ApiKeyPlaceholder);
        Assert.Equal(AssistantTexts.CloudPrivacy("Anthropic"), form.PrivacyNote);
        Assert.False(form.NeedsAddress);
        Assert.False(form.SaveCommand.CanExecute(null));
        Assert.Equal(AssistantTexts.NoChanges, form.SaveTip);
        Assert.Null(form.ErrorOf(nameof(AssistantSettingsViewModel.Model))); // an untouched form shows nothing

        form.Model = " ";
        Assert.Equal(AssistantTexts.EnterModel, form.ErrorOf(nameof(AssistantSettingsViewModel.Model)));
        Assert.False(form.SaveCommand.CanExecute(null));
        Assert.Equal(AssistantTexts.EnterModel, form.SaveTip);
        form.Model = "claude-sonnet-5";

        form.Provider = AssistantProvider.Ollama;
        Assert.True(form.NeedsAddress);
        Assert.False(form.NeedsKey);
        Assert.Equal(AssistantTexts.OllamaPrivacy, form.PrivacyNote);
        Assert.Equal(AssistantTexts.EnterAddress, form.ErrorOf(nameof(AssistantSettingsViewModel.Address)) ?? AssistantTexts.EnterAddress);
        form.Address = "10.0.0.5:11434";
        Assert.Equal(AssistantTexts.AddressNotValid, form.ErrorOf(nameof(AssistantSettingsViewModel.Address)));
        form.Address = "http://10.0.0.5:11434";
        Assert.Null(form.ErrorOf(nameof(AssistantSettingsViewModel.Address)));

        form.DailyLimit = "lots";
        Assert.Equal(AssistantTexts.LimitRange, form.ErrorOf(nameof(AssistantSettingsViewModel.DailyLimit)));
        form.DailyLimit = "500000";
        form.UserDailyLimit = "12";
        Assert.Equal(AssistantTexts.UserLimitRange, form.ErrorOf(nameof(AssistantSettingsViewModel.UserDailyLimit)));
        form.UserDailyLimit = "";
        Assert.True(form.SaveCommand.CanExecute(null));

        await form.SaveCommand.ExecuteAsync(null);
        var saved = (await api.GetAssistantSettingsAsync(CancellationToken.None)).Settings;
        Assert.Equal(("ollama", "http://10.0.0.5:11434", "claude-sonnet-5", 500_000L, 0L), (saved.Provider, saved.Address, saved.Model, saved.DailyLimit, saved.UserDailyLimit));
        Assert.Equal(["llama4:16x17b", "qwen3:32b"], form.Models);

        await form.TurnOffCommand.ExecuteAsync(null);
        Assert.False(form.IsOn);
        Assert.False(availability.IsOn);
        Assert.True(form.ShowTurnOn);
    }

    [Fact]
    public async Task TestShowsTheAnswerOrTheProblemAndNeverSaves()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        var form = new AssistantSettingsViewModel(api, Session(admin: true));
        await form.LoadAsync();
        form.Model = "no-tools";
        await form.TestCommand.ExecuteAsync(null);
        Assert.False(form.TestOk);
        Assert.Equal(FakeOadmApi.AssistantNoTools, form.TestResult);
        Assert.Equal("no-tools", api.AssistantTests.Single().Model);
        Assert.Equal("claude-opus-5-5", (await api.GetAssistantSettingsAsync(CancellationToken.None)).Settings.Model);

        form.Model = "claude-opus-5-5";
        await form.TestCommand.ExecuteAsync(null);
        Assert.True(form.TestOk);
        Assert.StartsWith("Hello from claude-opus-5-5", form.TestResult, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("provider", nameof(AssistantSettingsViewModel.Provider))]
    [InlineData("address", nameof(AssistantSettingsViewModel.Address))]
    [InlineData("model", nameof(AssistantSettingsViewModel.Model))]
    [InlineData("api_key", nameof(AssistantSettingsViewModel.ApiKey))]
    [InlineData("daily_limit", nameof(AssistantSettingsViewModel.DailyLimit))]
    [InlineData("user_daily_limit", nameof(AssistantSettingsViewModel.UserDailyLimit))]
    public async Task AServerRefusalShowsBelowTheFieldItNames(string trailer, string property)
    {
        var api = Substitute.For<IOadmApi>();
        api.GetAssistantSettingsAsync(Arg.Any<CancellationToken>()).Returns(new AssistantSettingsState
        {
            Settings = new AssistantSettings { Provider = "openai-compatible", Address = "http://10.0.0.5:1234", Model = "m", DailyLimit = 2_000_000 },
            HasApiKey = true,
        });
        api.SetAssistantSettingsAsync(Arg.Any<AssistantSettings>(), Arg.Any<CancellationToken>()).Returns<AssistantSettingsState>(_ =>
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Refused here."), new Metadata { { "oadm-field", trailer } }));
        var form = new AssistantSettingsViewModel(api, Session(admin: true));
        await form.LoadAsync();
        await form.TurnOnCommand.ExecuteAsync(null);
        Assert.Equal("Refused here.", form.ErrorOf(property));
        Assert.Null(form.Message); // never under the buttons
    }

    [Fact]
    public async Task OtherUsersReadTheSectionAndAnOlderServerHidesIt()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        var form = new AssistantSettingsViewModel(api, Session(admin: false));
        await form.LoadAsync();
        Assert.False(form.IsAdmin);
        Assert.False(form.ShowTurnOff);
        Assert.False(form.SaveCommand.CanExecute(null));
        Assert.Equal(("Anthropic", "On"), (form.ProviderText, form.StatusText));

        var old = Substitute.For<IOadmApi>();
        old.GetAssistantSettingsAsync(Arg.Any<CancellationToken>()).Returns<AssistantSettingsState>(_ => throw new RpcException(new Status(StatusCode.Unimplemented, "")));
        var hidden = new AssistantSettingsViewModel(old, Session(admin: true));
        await hidden.LoadAsync();
        Assert.False(hidden.IsAvailable);
    }

    // ------------------------------------------------------------ Markdown

    [Fact]
    public void TheMarkdownSubsetParsesParagraphsListsCodeAndTables()
    {
        var blocks = MarkdownDocument.Parse("# Devices\nTwo are **offline** and one runs `11.9`.\n\n- first\n- second *item*\n\n1. one\n2. two\n\n```json\n{\"a\": 1}\n```\n\n| Name | Count |\n|---|---:|\n| Ok | 42 |\n| Off | 3 |\n");
        Assert.Equal([typeof(MarkdownHeading), typeof(MarkdownParagraph), typeof(MarkdownList), typeof(MarkdownList), typeof(MarkdownCode), typeof(MarkdownTable)],
            blocks.Select(b => b.GetType()));
        var paragraph = (MarkdownParagraph)blocks[1];
        Assert.Contains(paragraph.Spans, s => s is { Text: "offline", Bold: true });
        Assert.Contains(paragraph.Spans, s => s is { Text: "11.9", Code: true });
        var bullets = (MarkdownList)blocks[2];
        Assert.False(bullets.Ordered);
        Assert.Contains(bullets.Items[1], s => s is { Text: "item", Italic: true });
        Assert.True(((MarkdownList)blocks[3]).Ordered);
        Assert.Equal("{\"a\": 1}", ((MarkdownCode)blocks[4]).Text);
        var table = (MarkdownTable)blocks[5];
        Assert.Equal([false, true], table.RightAligned);
        Assert.Equal("42", MarkdownDocument.PlainText(table.Rows[0][1]));

        // A text that is still streaming: an open code block runs to the end, a half bold stays plain.
        Assert.IsType<MarkdownCode>(MarkdownDocument.Parse("```\nline")[0]);
        Assert.Equal("a **b", MarkdownDocument.PlainText(((MarkdownParagraph)MarkdownDocument.Parse("a **b")[0]).Spans));
        Assert.Equal("snake_case_name", MarkdownDocument.PlainText(((MarkdownParagraph)MarkdownDocument.Parse("snake_case_name")[0]).Spans));
    }

    // ------------------------------------------------------------ headless

    [Fact]
    public async Task ThePanelShowsSuggestionsAnAnswerWithATableAndAPictureAndAPreviewCardBeforeAndAfterRun()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5), seedSampleData: false);
            using var f = new DevicesFixture(api);
            var devices = Devices(60);
            f.SeedDevices(devices);
            var panel = Panel(api, f);
            var window = PanelWindow(panel);
            window.Show();
            Pump();
            var view = window.GetVisualDescendants().OfType<AssistantPanelView>().Single();
            var texts = VisibleTexts(window);
            Assert.All(AssistantTexts.Suggestions, s => Assert.Contains(s, texts));
            Assert.Contains("primary", view.FindControl<Button>("Send")!.Classes);
            Assert.False(view.FindControl<Button>("Send")!.IsEffectivelyEnabled); // nothing written yet
            Save(window, outDir, "assistant-panel-empty.png");

            f.Select(devices[0].Id, devices[1].Id, devices[2].Id);
            api.AssistantScript = (_, ct) => Script(
            [
                new AssistantEvent { Tool = new AssistantToolLine { Text = "Looking at devices" } },
                new AssistantEvent { Table = Table(60, [.. devices.Take(50)]) },
                new AssistantEvent { Text = "**60 devices** are offline. Most are at the site *Building B*:\n\n- 41 at `Building B`\n- 19 here\n\n| Model | Devices |\n|---|---:|\n| P3265-LVE | 30 |\n| M3086-V | 30 |\n" },
                new AssistantEvent { Tool = new AssistantToolLine { Text = "Taking a picture" } },
                new AssistantEvent { Picture = new AssistantPicture { DeviceId = devices[0].Id, DeviceName = devices[0].Address, Jpeg = ByteString.CopyFrom(Jpeg()) } },
                new AssistantEvent { Text = "This is the picture of " + devices[0].Address + "." },
            ], ct);
            panel.Draft = "Which devices are offline?";
            await panel.SendCommand.ExecuteAsync(null);
            Pump();
            texts = VisibleTexts(window);
            Assert.Contains("3 devices selected", texts);
            Save(window, outDir, "assistant-panel-answer.png"); // the list follows the end of the answer
            var conversation = view.FindControl<ListBox>("Conversation")!;
            var scroller = conversation.GetVisualDescendants().OfType<ScrollViewer>().First();
            scroller.Offset = new Avalonia.Vector(0, 0);
            Pump();
            Assert.Contains("Looking at devices", VisibleTexts(window));
            Assert.Contains(window.GetVisualDescendants().OfType<DataGrid>(), g => g.IsEffectivelyVisible);
            Save(window, outDir, "assistant-panel-answer-table.png");

            panel.NewChatCommand.Execute(null);
            var chosen = devices.Take(3).ToList();
            var card = new AssistantPreviewCard
            {
                Handle = api.AddAssistantPreview(chosen.Select(d => d.Id)), Title = "Restart", DeviceCount = 3,
                Skipped = { "1 device: Site offline" }, Warnings = { "The cameras record nothing while they restart." },
            };
            card.FirstDevices.AddRange(chosen.Select(d => d.Address));
            api.AssistantScript = (_, ct) => Script(
            [
                new AssistantEvent { Tool = new AssistantToolLine { Text = "Checking 4 devices for the restart" } },
                new AssistantEvent { Preview = card },
                new AssistantEvent { Text = "Press **Run** to restart them." },
            ], ct);
            panel.Draft = "Restart these cameras";
            await panel.SendCommand.ExecuteAsync(null);
            Pump();
            var preview = panel.Items.OfType<AssistantPreviewItem>().Single();
            Assert.Contains("Restart", VisibleTexts(window));
            Assert.DoesNotContain("primary", view.FindControl<Button>("Send")!.Classes); // Run is the main button now
            Assert.Single(window.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && b.Classes.Contains("primary"));
            Save(window, outDir, "assistant-panel-preview.png");

            await panel.RunPreviewCommand.ExecuteAsync(preview);
            foreach (var task in (await api.ListTasksAsync(CancellationToken.None)).Where(t => preview.TaskIds.Contains(t.Id)))
            {
                var row = task.Clone();
                row.State = row.DeviceId == devices[0].Id ? TaskState.Running : TaskState.Done;
                f.Tasks.Apply(new TaskChanged { Kind = TaskChanged.Types.Kind.Added, Task = row });
            }

            Pump();
            texts = VisibleTexts(window);
            Assert.Contains(AssistantTexts.ShowTasks, texts);
            Assert.Contains("Running: 2 of 3 ended", texts);
            Save(window, outDir, "assistant-panel-preview-run.png");

            panel.EnlargeCommand.Execute(new AssistantPictureItem(devices[0].Id, devices[0].Address, Jpeg()));
            Assert.NotNull(panel.Enlarged?.Picture);
            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task TheTitleBarButtonOpensThePanelOnEveryPage()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
            using var f = new DevicesFixture(api);
            using var connection = new ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, NullLogger<ServerConnection>.Instance);
            var session = Session(admin: true);
            var panel = new AssistantPanelViewModel(api, new AssistantAvailability(), session, f.Devices, f.Tasks);
            var shell = f.CreateShell(connection, session, assistant: panel);
            var main = new Oadm.Client.Shell.MainWindow { DataContext = shell, Width = 1600, Height = 900 };
            main.Show();
            Pump();
            var button = main.FindControl<Oadm.Sdk.Client.Controls.ToolbarButton>("TitleAssistant")!;
            Assert.False(button.IsVisible); // not read yet: hidden

            await panel.RefreshAsync(CancellationToken.None);
            Pump();
            Assert.True(button.IsEffectivelyVisible);
            button.Command!.Execute(null);
            Pump();
            Assert.True(panel.IsOpen);
            Assert.Contains("open", main.FindControl<Border>("AssistantPanel")!.Classes);
            shell.NavigateCommand.Execute(shell.BottomNavItems.Single(i => i.Title == "Settings"));
            Pump();
            Assert.True(panel.IsOpen); // it stays across pages
            for (int i = 0; i < 30; i++)
            {
                Pump(); // the panel's slide-in transition runs on the render timer
            }

            Save(main, outDir, "assistant-main-window.png");
            button.Command.Execute(null);
            Assert.False(panel.IsOpen);
            main.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task TheSettingsCardShowsTheSectionAssistantBelowMcp()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            foreach (var (admin, name) in new[] { (true, "client-page-settings-ai-assistant.png"), (false, "client-page-settings-ai-assistant-operator.png") })
            {
                var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
                using var f = new DevicesFixture(api);
                using var connection = new ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, NullLogger<ServerConnection>.Instance);
                var session = Session(admin);
                var card = new AiAgentsViewModel(api, Substitute.For<IDialogService>(), session);
                var settings = new SettingsViewModel(f.Api, connection, NullLogger<SettingsViewModel>.Instance, session, aiAgents: card);
                await settings.LoadAsync();
                var window = new Window { Width = 1520, Height = 1700, Content = new SettingsView { DataContext = settings, Margin = new Avalonia.Thickness(24) } };
                window.Show();
                Pump();
                var view = Assert.Single(window.GetVisualDescendants().OfType<AiAgentsCardView>(), c => c.IsEffectivelyVisible);
                view.GetVisualDescendants().OfType<CollapsibleCard>().Single().IsExpanded = true;
                Pump();
                Assert.True(view.FindControl<Control>("AssistantSection")!.IsEffectivelyVisible);
                Assert.Equal(admin, view.FindControl<ComboBox>("AssistantModel")!.IsEffectivelyVisible);
                Assert.Equal(admin, view.FindControl<Button>("AssistantSave")!.IsEffectivelyVisible);
                if (admin)
                {
                    card.Assistant.Provider = AssistantProvider.OpenAiCompatible;
                    card.Assistant.Address = "10.0.0.5";
                    card.Assistant.DailyLimit = "12";
                    Pump();
                    var address = window.GetVisualDescendants().OfType<Oadm.Sdk.Client.Controls.FormField>().Single(x => x.Label == "Address" && x.IsEffectivelyVisible);
                    Assert.Contains(AssistantTexts.AddressNotValid, address.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text));
                    var limit = window.GetVisualDescendants().OfType<Oadm.Sdk.Client.Controls.FormField>().Single(x => x.Label == "Daily limit" && x.IsEffectivelyVisible);
                    Assert.Contains(AssistantTexts.LimitRange, limit.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text));
                    view.FindControl<Control>("AssistantSection")!.BringIntoView();
                    Pump();
                    Save(window, outDir, "client-page-settings-ai-assistant-errors.png");
                    card.Assistant.Provider = AssistantProvider.Anthropic;
                    card.Assistant.Address = "";
                    card.Assistant.DailyLimit = "2000000";
                    Pump();
                }

                view.FindControl<Control>("AssistantSection")!.BringIntoView();
                Pump();
                Save(window, outDir, name);
                window.Close();
            }

            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    private static Window PanelWindow(AssistantPanelViewModel panel)
    {
        var host = new Border { Classes = { "sidePanel", "open" }, Child = new Border { Classes = { "sidePanelContent" }, Child = new AssistantPanelView { DataContext = panel } } };
        return new Window { Width = 640, Height = 900, Content = new Border { Padding = new Avalonia.Thickness(16), Child = host } };
    }

    private static List<string?> VisibleTexts(Window window) =>
        [.. window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text)];

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Save(Window window, string? outDir, string name)
    {
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            frame.Save(Path.Combine(outDir, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }
}
