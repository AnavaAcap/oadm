using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Grpc.Core;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

using Oadm.Contracts.Assistant;
using Oadm.Server.Assistant;
using Oadm.Server.Tools;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Assistant;

/// <summary>
/// Looking on axis.com (docs/spec/assistant.md "Looking on axis.com"): the offer is an assistant-only tool, the
/// panel's card asks first, and only the user's Look up online gives exactly one answer the provider's web tools, restricted
/// to developer.axis.com and help.axis.com. The fake model records the tool list of every request; nothing goes online.
/// </summary>
public sealed class AssistantDocsLookupTests
{
    private static readonly string[] WebTools = ["web_search", "web_fetch"];

    private static JsonObject Topic(string topic = "turn the privacy mask on") => new() { ["topic"] = topic };

    private static Proto.AssistantDocsOffer OfferOf(IEnumerable<Proto.AssistantEvent> events) =>
        Assert.Single(events, e => e.KindCase == Proto.AssistantEvent.KindOneofCase.DocsOffer).DocsOffer;

    [Fact]
    public async Task TheOfferIsATooOfTheAssistantOnlyNeverOfTheRegistryOrMcp()
    {
        await using var server = await AssistantTestServer.StartAsync();
        Assert.DoesNotContain(server.Host.Get<ToolRegistry>().All, t => t.Name == AssistantDocsLookup.OfferName);

        await server.ChatAsync("Hello");
        var request = Assert.Single(server.Model.Requests);
        Assert.Contains(AssistantDocsLookup.OfferName, request.Tools);
        Assert.DoesNotContain(request.Tools, WebTools.Contains);
        Assert.Contains("Never go online without the user's yes", request.AllText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyTheClickGivesExactlyTheNextAnswerTheWebToolsOfTheAxisDocs()
    {
        await using var server = await AssistantTestServer.StartAsync();
        server.Model.Call((AssistantDocsLookup.OfferName, Topic())).Say("OADM has no function for this.");
        var asked = await server.ChatAsync("Turn the privacy mask on");
        var offer = OfferOf(asked);
        Assert.Equal("turn the privacy mask on", offer.Topic);
        Assert.NotEmpty(offer.OfferId);
        Assert.NotEmpty(offer.ExpiresUtc);

        // The answer with the offer: no web tool, the model is told to stop, no tool after it.
        var first = server.Model.Requests;
        Assert.Equal(2, first.Count);
        Assert.All(first, r => Assert.DoesNotContain(r.Tools, WebTools.Contains));
        Assert.Contains("Stop here", Assert.Single(first[1].Results()).Value["note"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(ChatToolMode.None, first[1].Options!.ToolMode);
        Assert.Equal(first[0].Tools, first[1].Tools); // declared, never dropped: Anthropic refuses tool calls without tools
        Assert.All(server.Providers.Connections, c => Assert.False(c.Online));

        // Look up online: this answer, with all its rounds, carries the web tools of the two domains.
        server.Model.Stream(new WebSearchToolCallContent("srv-1"), new TextContent("The privacy mask is set with "),
            new FunctionCallContent("call-web-1", "oadm_devices_summary", new Dictionary<string, object?>())).Say("privacymask.cgi (developer.axis.com).");
        var looked = await server.ChatAsync(AssistantTexts.LookUpMessage, docsOffer: offer.OfferId);
        Assert.Equal(string.Empty, looked[^1].Done.Error);
        var online = server.Model.Requests.Skip(2).ToList();
        Assert.Equal(2, online.Count);
        foreach (var request in online)
        {
            var web = request.Options!.Tools!.Where(t => WebTools.Contains(t.Name)).ToList();
            Assert.Equal(WebTools.Order(StringComparer.Ordinal), web.Select(t => t.Name).Order(StringComparer.Ordinal));
            Assert.All(web, t => Assert.Equal(AssistantTexts.DocsDomains, AssistantDocsLookup.DomainsOf(t)));
            Assert.DoesNotContain(AssistantDocsLookup.OfferName, request.Tools);
            Assert.Contains("turn the privacy mask on", request.Messages[0].Text, StringComparison.Ordinal);
            Assert.Contains(AssistantDocsLookup.WhereToLook, request.Messages[0].Text, StringComparison.Ordinal);
        }

        Assert.True(server.Providers.Connections[^1].Online);
        Assert.Contains(looked, e => e.KindCase == Proto.AssistantEvent.KindOneofCase.Tool && e.Tool.Text == AssistantTexts.LookingInDocs);
        Assert.Contains(AssistantSources.AxisDocs, Assert.Single(looked, e => e.KindCase == Proto.AssistantEvent.KindOneofCase.Sources).Sources.Chips);

        // The following answer has no web tool and no note of the lookup; the search step was not kept.
        await server.ChatAsync("Thanks");
        var after = server.Model.Requests[^1];
        Assert.DoesNotContain(after.Tools, WebTools.Contains);
        Assert.Contains(AssistantDocsLookup.OfferName, after.Tools);
        Assert.DoesNotContain("said yes", after.Messages[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain(AssistantDocsLookup.WhereToLook, after.AllText(), StringComparison.Ordinal);
        Assert.DoesNotContain(after.Messages.SelectMany(m => m.Contents), c => c is WebSearchToolCallContent);
        Assert.Contains("privacymask.cgi", after.AllText(), StringComparison.Ordinal);
        Assert.False(server.Providers.Connections[^1].Online);

        // The same offer a second time is refused; nothing reaches the model.
        var count = server.Model.Requests.Count;
        var again = await Assert.ThrowsAsync<RpcException>(() => server.ChatAsync(AssistantTexts.LookUpMessage, docsOffer: offer.OfferId));
        Assert.Equal(StatusCode.FailedPrecondition, again.StatusCode);
        Assert.Equal(AssistantTexts.DocsOfferEnded, again.Status.Detail);
        Assert.Equal(count, server.Model.Requests.Count);
    }

    [Fact]
    public async Task AnOfferBelongsToItsChatAndEndsWithTheNextMessageOrItsTime()
    {
        await using var server = await AssistantTestServer.StartAsync();
        server.Model.Call((AssistantDocsLookup.OfferName, Topic())).Say("Shall I?");
        var offer = OfferOf(await server.ChatAsync("Turn the privacy mask on"));

        // Another user's session cannot take it.
        var other = await server.Host.InvokerForUserAsync("other-admin", Oadm.Sdk.Plugins.UserRole.Admin);
        var foreign = await Assert.ThrowsAsync<RpcException>(() => server.ChatAsync(AssistantTexts.LookUpMessage, invoker: other, docsOffer: offer.OfferId));
        Assert.Equal(AssistantTexts.DocsOfferEnded, foreign.Status.Detail);

        // A message without the click ends it.
        await server.ChatAsync("Never mind");
        var ended = await Assert.ThrowsAsync<RpcException>(() => server.ChatAsync(AssistantTexts.LookUpMessage, docsOffer: offer.OfferId));
        Assert.Equal(AssistantTexts.DocsOfferEnded, ended.Status.Detail);
        Assert.All(server.Model.Requests, r => Assert.DoesNotContain(r.Tools, WebTools.Contains));
    }

    [Fact]
    public async Task AnOfferEndsAfterItsTime()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var server = await AssistantTestServer.StartAsync(configureServices: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(time);
        });
        server.Model.Call((AssistantDocsLookup.OfferName, Topic())).Say("Shall I?");
        var offer = OfferOf(await server.ChatAsync("Turn the privacy mask on"));
        time.Advance(AssistantDocsLookup.OfferLifetime + TimeSpan.FromSeconds(1));
        var expired = await Assert.ThrowsAsync<RpcException>(() => server.ChatAsync(AssistantTexts.LookUpMessage, docsOffer: offer.OfferId));
        Assert.Equal(AssistantTexts.DocsOfferEnded, expired.Status.Detail);
    }

    [Fact]
    public async Task OneAnswerShowsOneOffer()
    {
        await using var server = await AssistantTestServer.StartAsync();
        server.Model.Call((AssistantDocsLookup.OfferName, Topic()), (AssistantDocsLookup.OfferName, Topic("again"))).Say("Shall I?");
        var events = await server.ChatAsync("Turn the privacy mask on");
        OfferOf(events);
        Assert.Contains(server.Model.Requests[1].Results().Values, r => r["note"]!.GetValue<string>() == AssistantDocsLookup.OnceNote);
    }

    [Theory]
    [InlineData(AssistantTexts.Gemini)]
    [InlineData(AssistantTexts.Ollama)]
    [InlineData(AssistantTexts.OpenAiCompatible)]
    public async Task AProviderWithoutWebToolsSaysSoInOneLineAndShowsNoCard(string provider)
    {
        await using var server = await AssistantTestServer.StartAsync(provider: provider);
        server.Model.Call((AssistantDocsLookup.OfferName, Topic())).Say("OADM has no function for this.");
        var events = await server.ChatAsync("Turn the privacy mask on");
        Assert.DoesNotContain(events, e => e.KindCase == Proto.AssistantEvent.KindOneofCase.DocsOffer);
        Assert.Contains(events, e => e.KindCase == Proto.AssistantEvent.KindOneofCase.Notice && e.Notice == AssistantTexts.CannotLookOnline(provider));
        Assert.Contains("cannot look online", AssistantTexts.CannotLookOnline(provider), StringComparison.Ordinal);
        Assert.Empty(AssistantDocsLookup.ToolsFor(provider));
        Assert.All(server.Model.Requests, r => Assert.DoesNotContain(r.Tools, WebTools.Contains));
    }

    [Fact]
    public async Task ADocsReadThatFailsIsAPlainLineNeverNoInternet()
    {
        await using var server = await AssistantTestServer.StartAsync();
        server.Model.Call((AssistantDocsLookup.OfferName, Topic())).Say("Shall I?");
        var offer = OfferOf(await server.ChatAsync("Turn the privacy mask on"));
        var failed = new WebSearchToolResultContent("srv-1") { Outputs = [new ErrorContent(null) { ErrorCode = "unavailable" }] };
        server.Model.Stream(new WebSearchToolCallContent("srv-1"), failed, new TextContent("The Axis docs could not be read."));
        var events = await server.ChatAsync(AssistantTexts.LookUpMessage, docsOffer: offer.OfferId);
        Assert.Contains(events, e => e.KindCase == Proto.AssistantEvent.KindOneofCase.Tool && !e.Tool.Failed && e.Tool.Text == AssistantTexts.DocsPageNotRead);
        Assert.DoesNotContain(events, e => e.KindCase == Proto.AssistantEvent.KindOneofCase.Tool && e.Tool.Failed);
    }

    [Fact]
    public void TheProvidersWebToolsNameOnlyTheAxisDocs()
    {
        var anthropic = AssistantDocsLookup.ToolsFor(AssistantTexts.Anthropic);
        Assert.Equal(WebTools, anthropic.Select(t => t.Name));
        Assert.All(anthropic, t => Assert.Equal(AssistantTexts.DocsDomains, AssistantDocsLookup.DomainsOf(t)));
        var openAi = Assert.Single(AssistantDocsLookup.ToolsFor(AssistantTexts.OpenAi));
        Assert.Equal(AssistantTexts.DocsDomains, AssistantDocsLookup.DomainsOf(openAi));
        Assert.Equal(["axis.com"], AssistantTexts.DocsDomains); // its subdomains (help, developer, www) are part of it for both providers
    }

    [Fact]
    public void EveryPageOfTheMapIsOnAxisComAndTheNoteNamesIt()
    {
        var addresses = Regex.Matches(AssistantDocsLookup.WhereToLook, @"[a-z0-9.-]+\.[a-z]{2,}/[^\s;,)]*")
            .Select(m => m.Value).ToList();
        Assert.True(addresses.Count >= 12, $"{addresses.Count} addresses in the map");
        foreach (var address in addresses)
        {
            var host = new Uri("https://" + address).Host;
            Assert.True(AssistantTexts.DocsDomains.Any(d => host == d || host.EndsWith("." + d, StringComparison.Ordinal)), $"{host} is not on axis.com");
        }

        Assert.Contains(AssistantDocsLookup.WhereToLook, AssistantDocsLookup.OnlineNote("reset button"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheAnthropicFetchReadsAtMostTheContentLimitOfAPage()
    {
        var fetch = Assert.Single(AssistantDocsLookup.ToolsFor(AssistantTexts.Anthropic), t => t.Name == "web_fetch");
        var json = fetch.GetService<Anthropic.Models.Messages.ToolUnion>()!.Json;
        Assert.Equal(AssistantDocsLookup.MaxContentTokens, json.GetProperty("max_content_tokens").GetInt32());
        Assert.Equal(20_000, AssistantDocsLookup.MaxContentTokens);
    }

    [Fact]
    public void AnthropicGetsTheWebToolsOfTheCurrentModels()
    {
        var types = AssistantDocsLookup.ToolsFor(AssistantTexts.Anthropic)
            .Select(t => t.GetService<Anthropic.Models.Messages.ToolUnion>()!.Json.GetProperty("type").GetString()).ToList();
        Assert.Equal(["web_search_20260209", "web_fetch_20260209"], types);
    }

    [Fact]
    public void TheChatKeepsTheTextAndOadmsCallsOfALookupNeverTheProvidersSteps()
    {
        List<ChatMessage> turn =
        [
            new(ChatRole.User, "Look it up"),
            new(ChatRole.Assistant, [new WebSearchToolCallContent("srv-1"), new WebSearchToolResultContent("srv-1"), new TextContent("Found it."),
                new FunctionCallContent("c1", "oadm_devices_summary")]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "{}")]),
            new(ChatRole.Assistant, [new WebSearchToolCallContent("srv-2")]),
        ];
        var kept = AssistantChats.WithoutWebTools(turn);
        Assert.Equal(3, kept.Count);
        Assert.DoesNotContain(kept.SelectMany(m => m.Contents), c => c is WebSearchToolCallContent or WebSearchToolResultContent);
        Assert.Contains(kept.SelectMany(m => m.Contents), c => c is FunctionCallContent);
        Assert.Contains(kept.SelectMany(m => m.Contents), c => c is FunctionResultContent);
    }
}
