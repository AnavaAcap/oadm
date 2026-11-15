using Oadm.Core.Plugins;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.NtpServer.Tests;

/// <summary>
/// "Use OADM as NTP server" has no dialog: the client runs it with no payload. The task has no check of its own before the
/// run, so the default accepts what the client sends (nothing), and an empty object.
/// </summary>
public sealed class DialogPayloadValidationTests : IAsyncLifetime
{
    private NtpServerPlugin _plugin = null!;

    public async Task InitializeAsync()
    {
        _plugin = new NtpServerPlugin(Options.Test());
        await _plugin.StartAsync(new TestCoreContext(new InMemoryPluginSettingsProvider().GetSettings(NtpServerPluginInfo.PluginId), null), CancellationToken.None);
    }

    public async Task DisposeAsync() => await _plugin.DisposeAsync();

    [Fact]
    public void The_task_runs_without_a_dialog_and_accepts_no_payload_and_an_empty_one()
    {
        var task = Assert.Single(_plugin.TaskPlugins);
        var caller = FakeCallContext.AllDevices(new FakeCoreContext());

        Assert.False(task.RequiresDialog);
        Assert.Empty(task.ValidatePayload(null, caller));
        Assert.Empty(task.ValidatePayload("{}", caller));
    }
}
