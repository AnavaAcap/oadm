using Oadm.Server.Assistant;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Assistant;

/// <summary>
/// The answer's follow-up questions and sources (docs/spec/assistant.md "What the panel shows"): the model's &lt;&lt;next&gt;&gt;
/// line never reaches the panel as text, wherever the stream cuts it; a marker inside a line stays text; at most three
/// questions; the sources name the areas of the tools that answered, sent once before done.
/// </summary>
public sealed class AssistantFollowUpTests
{
    private static (string Text, IReadOnlyList<string> Questions) Run(params string[] pieces)
    {
        var filter = new FollowUpFilter();
        var text = string.Concat(pieces.Select(filter.Feed)) + filter.Finish();
        return (text, filter.Questions);
    }

    [Fact]
    public void TheMarkerLineIsTakenOutWhereverTheStreamCutsIt()
    {
        const string answer = "3 cameras run old AXIS OS.\n<<next>> Upgrade them | Which ones are offline?";
        for (int cut = 1; cut < answer.Length; cut++)
        {
            for (int second = cut + 1; second <= answer.Length; second++)
            {
                var (text, questions) = Run(answer[..cut], answer[cut..second], answer[second..]);
                Assert.Equal("3 cameras run old AXIS OS.\n", text);
                Assert.Equal(["Upgrade them", "Which ones are offline?"], questions);
            }
        }

        // One character at a time, the marker in another case and spaces before it.
        var (oneByOne, asked) = Run([.. "Done.\n  <<NEXT>> Restart them\n".Select(c => c.ToString())]);
        Assert.Equal("Done.\n", oneByOne);
        Assert.Equal(["Restart them"], asked);
    }

    [Fact]
    public void WithoutAMarkerEveryCharacterShowsAndNothingIsAsked()
    {
        var (text, questions) = Run("Two devices ", "are offline.\n", "<<", "nope");
        Assert.Equal("Two devices are offline.\n<<nope", text);
        Assert.Empty(questions);

        // A line that starts like the marker shows as soon as it cannot become one; nothing waits at the end.
        var filter = new FollowUpFilter();
        Assert.Equal("Done.\n", filter.Feed("Done.\n<<ne"));
        Assert.Equal("<<new rules", filter.Feed("w rules"));
        Assert.Equal(string.Empty, filter.Finish());
        Assert.Empty(filter.Questions);
    }

    [Fact]
    public void AMarkerInsideALineIsTextAndOnlyThreeQuestionsAreKept()
    {
        var (text, questions) = Run("Type <<next>> to see it.\nThat is all.");
        Assert.Equal("Type <<next>> to see it.\nThat is all.", text);
        Assert.Empty(questions);

        var (shown, kept) = Run("Here.\n<<next>> A | b |  | B | C | D | E");
        Assert.Equal("Here.\n", shown);
        Assert.Equal(["A", "b", "C"], kept); // empty and repeated ones out, at most 3

        Assert.Equal(new string('x', FollowUpFilter.MaxQuestionLength), Assert.Single(FollowUpFilter.Parse(new string('x', 500))));
    }

    [Fact]
    public async Task TheChatSendsTheSourcesAndTheQuestionsOnceBeforeDoneAndNeverTheMarker()
    {
        await using var server = await AssistantTestServer.StartAsync();
        await GroupCallers.DeviceAsync(server.Host, 71);
        await GroupCallers.DeviceAsync(server.Host, 72);
        server.Model
            .Call(("oadm_devices_search", null), (Oadm.Server.Tools.DocsTools.Name, AssistantTestServer.Args("""{"query":"upgrade"}""")))
            .SayPieces("Both cameras are fine.\n<", "<nex", "t>> Upgrade them | Show a pic", "ture");

        var events = await server.ChatAsync("How are my cameras?");

        Assert.Equal("Both cameras are fine.\n", AssistantTestServer.TextOf(events));
        var sources = Assert.Single(events, e => e.KindCase == Proto.AssistantEvent.KindOneofCase.Sources).Sources;
        Assert.Equal(["2 devices", "Docs"], sources.Chips);
        var next = Assert.Single(events, e => e.KindCase == Proto.AssistantEvent.KindOneofCase.FollowUps).FollowUps;
        Assert.Equal(["Upgrade them", "Show a picture"], next.Questions);
        Assert.Equal(Proto.AssistantEvent.KindOneofCase.FollowUps, events[^2].KindCase);
        Assert.Equal(Proto.AssistantEvent.KindOneofCase.Done, events[^1].KindCase);
        Assert.Contains("<<next>>", server.Model.Requests[0].Messages[0].Text, StringComparison.Ordinal); // the rule in the system prompt

        // No tool and no marker: neither event.
        server.Model.Say("Hello there.");
        var plain = await server.ChatAsync("Hi");
        Assert.DoesNotContain(plain, e => e.KindCase is Proto.AssistantEvent.KindOneofCase.Sources or Proto.AssistantEvent.KindOneofCase.FollowUps);
    }
}
