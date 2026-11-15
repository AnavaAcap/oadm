using System.Text.Json.Nodes;

using Oadm.Plugins.Radius.Tests.Support;
using Oadm.Plugins.Radius.Tools;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Radius.Tests;

/// <summary>The tool of the 802.1X server (docs/spec/mcp.md "Plugins"): its state and the log of the caller's devices, newest first, paged.</summary>
public sealed class ToolTests
{
    [Fact]
    public async Task TheLogToolGivesTheStateAndTheRowsOfTheCallersDevicesOnly()
    {
        await using var harness = await PluginHarness.CreateAsync(start: false);
        var inside = harness.AddDevice("ACCC8E000001");
        var outside = harness.AddDevice("ACCC8E000002");
        await harness.StartAsync();
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        using var foreign = new TestAuthority("Foreign CA");
        foreach (var certificate in new[] { harness.Ca.Camera(inside.Serial), harness.Ca.Camera(outside.Serial), foreign.Camera("ACCC8E999999") })
        {
            await harness.Switch().RunAsync(new TestSupplicant(new SupplicantOptions { Certificate = certificate, TrustedCa = harness.Ca.CaCertificate }));
        }

        harness.Tick();
        ICorePlugin core = harness.Plugin;
        Assert.Equal("log", Assert.Single(core.Tools).Name);
        Assert.Equal(UserRole.Operator, core.RequiredRole(RadiusTools.Log.Method));
        Assert.False(core.IsAudited(RadiusTools.Log.Method));

        async Task<JsonNode> ToolAsync(ICallContext call, string? payload = null) =>
            JsonNode.Parse((await harness.Ctx.InvokeAsync(harness.Plugin, call, RadiusTools.Log.Method, payload))!)!;

        var all = await ToolAsync(harness.Admin);
        Assert.True(all["running"]!.GetValue<bool>());
        Assert.Equal("Running on port 1812", all["status"]!.GetValue<string>());
        Assert.Equal(3, all["entries"]!.AsArray().Count);
        var failed = await ToolAsync(harness.Admin, """{"result":"failed"}""");
        var refusal = Assert.Single(failed["entries"]!.AsArray())!;
        Assert.False(string.IsNullOrEmpty(refusal["reason"]!.GetValue<string>()));

        // A page at a time: the rows of three pages of one, each once.
        var seen = new List<string>();
        string? after = null;
        for (var page = 0; page < 4; page++)
        {
            var payload = new JsonObject { ["limit"] = 1 };
            if (after is not null)
            {
                payload["after"] = after;
            }

            var answer = await ToolAsync(harness.Admin, payload.ToJsonString());
            seen.AddRange(answer["entries"]!.AsArray().Select(e => e!.ToJsonString()));
            after = answer["next"]?.GetValue<string>();
            if (after is null)
            {
                break;
            }
        }

        Assert.Equal(3, seen.Distinct().Count());

        var group = harness.Ctx.CallAsGroup([inside.Id]);
        var mine = await ToolAsync(group);
        Assert.Equal(inside.Id.ToString(), Assert.Single(mine["entries"]!.AsArray())!["device_id"]!.GetValue<string>());
        Assert.DoesNotContain(outside.Id.ToString(), mine.ToJsonString(), StringComparison.Ordinal);

        var bad = await ToolAsync(harness.Admin, """{"result":"maybe"}""");
        Assert.Equal("result", Assert.Single(bad["errors"]!.AsArray())!["field"]!.GetValue<string>());
    }

    [Fact]
    public async Task WhileTheServerIsOffTheToolSaysSoWithoutALog()
    {
        await using var harness = await PluginHarness.CreateAsync(h => h.TurnedOn = false);
        var answer = JsonNode.Parse((await harness.Ctx.InvokeAsync(harness.Plugin, harness.Admin, RadiusTools.Log.Method, null))!)!;
        Assert.False(answer["running"]!.GetValue<bool>());
        Assert.False(string.IsNullOrEmpty(answer["status"]!.GetValue<string>()));
        Assert.Empty(answer["entries"]!.AsArray());
    }
}
