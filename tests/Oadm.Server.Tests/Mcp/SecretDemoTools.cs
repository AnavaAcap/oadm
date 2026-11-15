using System.Text.Json.Nodes;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Server.Tools;

using static Oadm.Server.Tools.ToolSchema;

namespace Oadm.Server.Tests.Mcp;

/// <summary>
/// A change with a secret, the way a recipient's save with its password uses the secret channel (docs/spec/mcp.md "Secrets"):
/// the preview declares the slot <c>password</c>, the run reads it once with <see cref="ToolCall.Secret"/>. What the run got
/// is kept in <see cref="Received"/> so a test can see the value arrived (and nowhere else).
/// </summary>
internal sealed class SecretDemoTools(ToolHandles handles) : IToolSource
{
    public const string Preview = "oadm_demo_recipient_save_preview";
    public const string Run = "oadm_demo_recipient_save_run";

    /// <summary>The passwords the runs got, in order.</summary>
    public List<string?> Received { get; } = [];

    public IReadOnlyList<OadmTool> Current() =>
    [
        new OadmTool(
            Preview,
            "Preview saving a recipient",
            "Checks a recipient and shows what a save would do; nothing is changed. Its password comes through the secret channel.",
            Object(null, [P("name", Text("The recipient.", 64, minLength: 1))], "name"),
            OutObject(null,
            [
                P("name", Out("string", "The recipient.")),
                P("handle", Out("string", "Pass it to the run.")),
                P("expires", Out("string", "When the handle ends (UTC).")),
                .. ToolSecretAnswers.OutputProperties(),
            ], "name", "handle", "expires", "needs_secrets", "secret_links"),
            [],
            call =>
            {
                var handle = handles.AddChange(call.Caller.BindingKey, new PlannedChange(Run, call.Text("name")!));
                var answer = new JsonObject
                {
                    ["name"] = call.Text("name"),
                    ["handle"] = handle,
                    ["expires"] = ToolFiles.Time(handles.EndOfNew()),
                };
                ToolSecretAnswers.Declare(call, answer, handle, Run, [new ToolSecretSlot("password", "password")]);
                return Task.FromResult(answer);
            }),
        new OadmTool(
            Run,
            "Save a recipient",
            "Saves the recipient of the preview with the password the user set.",
            Object(null, [P("handle", Text("The handle of the preview.", 64, minLength: 1))], "handle"),
            OutObject(null, [P("saved", Out("string", "The recipient.")), P("has_password", Out("boolean", "A password was set."))], "saved", "has_password"),
            [],
            call =>
            {
                var name = handles.TakeChange<string>(call.Caller.BindingKey, call.Text("handle"), Run)
                    ?? throw new ToolException(ToolHandles.Ended, [new ToolFieldError("handle", ToolHandles.Ended)]);
                var password = call.Secret("password");
                lock (Received)
                {
                    Received.Add(password);
                }

                return Task.FromResult(new JsonObject { ["saved"] = name, ["has_password"] = password is not null });
            })
        {
            ReadsOnly = false,
            Idempotent = false,
        },
    ];
}

/// <summary>A task that keeps a result file of the given bytes (<see cref="ITaskExecutionContext.SaveResultFileAsync"/>).</summary>
internal sealed class ResultFileTask : ITaskPlugin
{
    public const string PluginId = "test.result-file";

    public static readonly byte[] Content = [.. Enumerable.Range(0, 300 * 1024).Select(i => (byte)(i * 7))]; // more than one chunk

    public string Id => PluginId;

    public string DisplayName => "Export a file";

    public string? IconKey => null;

    public bool ShowInToolbar => false;

    public bool RequiresDialog => false;

    public Type? PayloadType => typeof(Payload);

    public bool CanRun(IDeviceInfo device) => true;

    public async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        using var content = new MemoryStream(Content);
        await ctx.SaveResultFileAsync("config-" + device.Serial + ".txt", "text/plain", content, ct);
    }

    public sealed record Payload;
}
