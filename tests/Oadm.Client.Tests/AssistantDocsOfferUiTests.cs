using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;

using Oadm.Client.Api;
using Oadm.Client.Assistant;
using Oadm.Contracts.V1;

using static Oadm.Client.Tests.AssistantTests;

namespace Oadm.Client.Tests;

/// <summary>
/// The offer to look it up on axis.com (docs/spec/assistant.md "Looking on axis.com"): a card with the question,
/// Not now and Look up online; only the click sends the offer's id, Not now and another message send none. Headless
/// screenshots in OADM_SCREENSHOT_DIR (OADM_TEST_APPEARANCE=Light for the light ones).
/// </summary>
public sealed class AssistantDocsOfferUiTests
{
    private static AssistantEvent Offer(string id) => new()
    {
        DocsOffer = new AssistantDocsOffer { OfferId = id, Topic = "turn the privacy mask on", ExpiresUtc = "2026-11-15T12:10:00Z" },
    };

    [Fact]
    public async Task OnlyLookUpOnlineSendsTheOfferAndNotNowOrAnotherMessageSendNone()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var appearance = string.Equals(Environment.GetEnvironmentVariable("OADM_TEST_APPEARANCE"), "Light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5), seedSampleData: false);
            using var f = new DevicesFixture(api);
            f.SeedDevices(AssistantTests.Devices(2));
            var panel = await PanelAsync(api, f);
            var window = PanelWindow(panel);
            window.Show();
            Pump();
            var view = window.GetVisualDescendants().OfType<AssistantPanelView>().Single();

            api.AssistantScript = (_, ct) => Script([Offer("offer-1"), new AssistantEvent { Text = "OADM has no function for this." }], ct);
            panel.Draft = "Turn the privacy mask on";
            await panel.SendCommand.ExecuteAsync(null);
            Pump();

            var offer = panel.Items.OfType<AssistantDocsOfferItem>().Single();
            Assert.True(offer.IsReady);
            Assert.Contains(AssistantTexts.DocsOfferQuestion, VisibleTexts(window));
            Assert.Contains("turn the privacy mask on", VisibleTexts(window));
            var lookUp = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "LookUpOnline");
            var notNow = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "NotNow");
            Assert.True(lookUp.IsEffectivelyEnabled);
            Assert.Contains("primary", lookUp.Classes); // the panel's one accent button while it waits
            Assert.Contains("secondary", notNow.Classes);
            Assert.True(panel.SendIsQuiet);
            Save(window, outDir, $"assistant-docs-offer-{appearance}.png");

            // Look up online: the next message carries the offer's id; the card says so and its buttons go.
            api.AssistantScript = (_, ct) => Script([new AssistantEvent { Tool = new AssistantToolLine { Text = "Looking on axis.com" } },
                new AssistantEvent { Text = "Use **privacymask.cgi** (developer.axis.com/vapix/...)." }], ct);
            await panel.LookUpCommand.ExecuteAsync(offer);
            Pump();
            var sent = api.AssistantMessages[^1];
            Assert.Equal("offer-1", sent.DocsOfferId);
            Assert.Equal(AssistantTexts.LookUpMessage, sent.Text);
            Assert.False(offer.IsReady);
            Assert.True(offer.IsAccepted);
            Assert.Contains(AssistantTexts.LookedUpOnline, VisibleTexts(window));
            Assert.False(panel.SendIsQuiet);
            Save(window, outDir, $"assistant-docs-offer-looked-up-{appearance}.png");

            // Not now: nothing is sent.
            api.AssistantScript = (_, ct) => Script([Offer("offer-2"), new AssistantEvent { Text = "OADM has no function for this." }], ct);
            panel.Draft = "Set the siren profile";
            await panel.SendCommand.ExecuteAsync(null);
            Pump();
            var second = panel.Items.OfType<AssistantDocsOfferItem>().Last();
            var count = api.AssistantMessages.Count;
            panel.DeclineLookUpCommand.Execute(second);
            Pump();
            Assert.Equal(count, api.AssistantMessages.Count);
            Assert.False(second.IsReady);
            Assert.Contains(AssistantTexts.NotLookedUp, VisibleTexts(window));
            Save(window, outDir, $"assistant-docs-offer-not-now-{appearance}.png");

            // Another message while an offer waits: it goes without the offer, and the card ends.
            api.AssistantScript = (_, ct) => Script([Offer("offer-3"), new AssistantEvent { Text = "OADM has no function for this." }], ct);
            panel.Draft = "Change the overlay";
            await panel.SendCommand.ExecuteAsync(null);
            Pump();
            var third = panel.Items.OfType<AssistantDocsOfferItem>().Last();
            api.AssistantScript = (_, ct) => Script([new AssistantEvent { Text = "Fine." }], ct);
            panel.Draft = "Never mind";
            await panel.SendCommand.ExecuteAsync(null);
            Pump();
            Assert.Equal(string.Empty, api.AssistantMessages[^1].DocsOfferId);
            Assert.False(third.IsReady);
            Assert.False(third.IsAccepted);
            Assert.Single(api.AssistantMessages, m => m.DocsOfferId.Length > 0);
            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }
}
