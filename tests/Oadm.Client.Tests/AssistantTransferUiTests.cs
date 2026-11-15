using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Assistant;
using Oadm.Client.Dialogs;
using Oadm.Contracts.V1;

using static Oadm.Client.Tests.AssistantTests;

namespace Oadm.Client.Tests;

/// <summary>
/// Secrets and files in the Assistant panel (docs/spec/assistant.md rule 3, docs/spec/mcp.md "Secrets", "Files"): a preview
/// card's password field (validation below the field, Run disabled with its reason while it is empty, the value sent with Run
/// and forgotten afterwards, never in a message), and a file card with Save. Headless screenshots in OADM_SCREENSHOT_DIR.
/// </summary>
public sealed class AssistantTransferUiTests
{
    private const string Typed = "canary-typed-4c1f";

    [Fact]
    public async Task ThePasswordFieldOfACardGoesWithRunOnlyAndAFileCardSavesItsFile()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var saved = new MemoryStream();
        var dialogs = Substitute.For<IDialogService>();
        dialogs.CreateFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<FileType>()).Returns(new SavedFile("devices.csv", saved));
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5), seedSampleData: false);
            using var f = new DevicesFixture(api);
            var devices = AssistantTests.Devices(2);
            f.SeedDevices(devices);
            var panel = await PanelAsync(api, f, dialogs: dialogs);
            var window = PanelWindow(panel);
            window.Show();
            Pump();
            var view = window.GetVisualDescendants().OfType<AssistantPanelView>().Single();

            var card = new AssistantPreviewCard
            {
                Handle = api.AddAssistantPreview(devices.Select(d => d.Id), "password"), Title = "Save the recipient", CountText = "1 recipient",
                Details = { "Recipient: Alarm desk", "Sends to: mqtts://10.0.0.9:8883" },
                Secrets = { new AssistantSecretField { Name = "password", Label = "password" } },
            };
            api.AssistantScript = (_, ct) => Script(
            [
                new AssistantEvent { Preview = card },
                new AssistantEvent { Text = "Enter the password in the card and press **Run**." },
            ], ct);
            panel.Draft = "Save the recipient Alarm desk with a password";
            await panel.SendCommand.ExecuteAsync(null);
            Pump();

            // Empty: Run is disabled and says why; no error below the untouched field.
            var preview = panel.Items.OfType<AssistantPreviewItem>().Single();
            var run = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "RunPreview");
            Assert.False(run.IsEffectivelyEnabled);
            Assert.Equal("Enter the password.", ToolTip.GetTip(run));
            Assert.Contains("Password", VisibleTexts(window));
            Assert.DoesNotContain("Enter the password.", VisibleTexts(window));
            Save(window, outDir, "assistant-card-password-empty.png");

            // Typed and cleared again: the error below the field.
            var field = preview.Secrets.Single();
            field.Value = "x";
            field.Value = string.Empty;
            Pump();
            Assert.Contains("Enter the password.", VisibleTexts(window));
            Save(window, outDir, "assistant-card-password-error.png");

            field.Value = Typed;
            Pump();
            Assert.True(run.IsEffectivelyEnabled);
            Assert.Null(ToolTip.GetTip(run));
            Save(window, outDir, "assistant-card-password-filled.png");

            await panel.RunPreviewCommand.ExecuteAsync(preview);
            Pump();
            Assert.Equal(Typed, Assert.Single(api.AssistantSecretRuns).Secrets["password"]);
            Assert.Equal(string.Empty, field.Value); // forgotten once it went to the server
            Assert.True(preview.IsStarted);

            // A file of an answer: a card with Save that fetches it then.
            var reference = api.AddAssistantFile("oadm-devices-2026-11-15.csv", "MAC address,Status,Address\r\n"u8.ToArray());
            api.AssistantScript = (_, ct) => Script(
            [
                new AssistantEvent { File = new AssistantFile { FileRef = reference, Name = "oadm-devices-2026-11-15.csv", Size = 30, ContentType = "text/csv" } },
                new AssistantEvent { Text = "The device list is ready to save." },
            ], ct);
            panel.Draft = "Export the devices";
            await panel.SendCommand.ExecuteAsync(null);
            Pump();
            var file = panel.Items.OfType<AssistantFileItem>().Single();
            Assert.Contains("oadm-devices-2026-11-15.csv", VisibleTexts(window));
            Assert.Contains(Oadm.Sdk.Formatting.ByteSize.Text(30), VisibleTexts(window));
            Save(window, outDir, "assistant-file-card.png");

            await panel.SaveFileCommand.ExecuteAsync(file);
            Pump();
            Assert.Equal("MAC address,Status,Address\r\n"u8.ToArray(), saved.ToArray());
            Assert.True(file.IsSaved);
            Assert.Contains(AssistantTexts.FileSaved, VisibleTexts(window));
            Save(window, outDir, "assistant-file-card-saved.png");

            // The typed value went with Run only: never in a message of the chat.
            Assert.DoesNotContain(api.AssistantMessages, m => m.ToString().Contains(Typed, StringComparison.Ordinal));
            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }
}
