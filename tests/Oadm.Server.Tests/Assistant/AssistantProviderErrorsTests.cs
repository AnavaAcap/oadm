using Oadm.Server.Assistant;

namespace Oadm.Server.Tests.Assistant;

/// <summary>A refused request shows the provider's own reason, not only its status (the Anthropic SDK keeps it in ResponseBody).</summary>
public sealed class AssistantProviderErrorsTests
{
    private sealed class RefusedException(string body) : Exception("Status Code: BadRequest")
    {
        public int StatusCode { get; } = 400;

        public string ResponseBody { get; } = body;
    }

    [Fact]
    public void ABadRequestShowsTheProvidersReason()
    {
        var refused = new RefusedException("""{"type":"error","error":{"type":"invalid_request_error","message":"tools.1: web_fetch is not available.\nmore"}}""");

        Assert.Equal("The provider answered with an error: tools.1: web_fetch is not available.", AssistantProviderErrors.Plain(refused, null));
    }

    [Fact]
    public void ABodyWithoutTheProvidersJsonKeepsTheMessageAndTheKeyNeverShows()
    {
        Assert.Equal("The provider answered with an error: Status Code: BadRequest", AssistantProviderErrors.Plain(new RefusedException("<html>"), null));
        var withKey = new RefusedException("""{"error":{"message":"key sk-secret is wrong"}}""");
        Assert.Equal("The provider answered with an error: key *** is wrong", AssistantProviderErrors.Plain(withKey, "sk-secret"));
    }
}
