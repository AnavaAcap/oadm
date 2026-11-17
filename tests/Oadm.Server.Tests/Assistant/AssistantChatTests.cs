using System.Text;
using System.Text.Json.Nodes;

using Google.Protobuf;

using Grpc.Core;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Oadm.Contracts.Assistant;
using Oadm.Core.Auth;
using Oadm.Core.LiveView;
using Oadm.Core.LiveView.Rtsp;
using Oadm.Core.Vapix;
using Oadm.Server.LiveView;
using Oadm.Core.Persistence;
using Oadm.Server.Assistant;
using Oadm.Server.Tests.Support;
using Oadm.Server.Tools;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Assistant;

/// <summary>
/// The assistant's conversations (docs/spec/assistant.md "Tests") with a fake model that asks for given tool calls: the user's
/// rights, no run tool offered, Run only with the user's own handle, the selection cut to the group, pictures to the model
/// only with the setting, attachments as data, the limits of a message, Stop, End and the end of the session.
/// </summary>
public sealed class AssistantChatTests
{
    private static Proto.AssistantEvent.KindOneofCase Kind(Proto.AssistantEvent e) => e.KindCase;

    [Fact]
    public async Task TheModelsThinkingStaysInItsOwnAnswerAndIsNeverSentAgain()
    {
        await using var server = await AssistantTestServer.StartAsync();
        server.Model
            .Stream(new TextReasoningContent("weighing it") { ProtectedData = "signature-1" }, new FunctionCallContent("call-1", "oadm_server_info", new Dictionary<string, object?>()))
            .Say("The server runs.")
            .Say("Still runs.");

        await server.ChatAsync("How is the server?");
        await server.ChatAsync("And now?");

        // Within its answer the thinking goes back with the tool call (Anthropic needs it there); the next answer never has it.
        Assert.Contains(server.Model.Requests[1].Messages.SelectMany(m => m.Contents), c => c is TextReasoningContent);
        var later = server.Model.Requests[2].Messages.SelectMany(m => m.Contents).ToList();
        Assert.DoesNotContain(later, c => c is TextReasoningContent);
        Assert.Contains(later, c => c is FunctionCallContent { CallId: "call-1" });
    }

    [Fact]
    public async Task TheModelGetsReadsAndPreviewsButNeverARunOrAChangeAndTheAnswerStreams()
    {
        await using var server = await AssistantTestServer.StartAsync();
        server.Model.Say("Hello there.");

        var events = await server.ChatAsync("Hi");

        Assert.Equal("Hello there.", AssistantTestServer.TextOf(events));
        Assert.Equal(10, events[^1].Done.Tokens);
        Assert.Empty(events[^1].Done.Error);
        var offered = server.Model.Requests[0].Tools;
        Assert.Contains("oadm_devices_search", offered);
        Assert.Contains("oadm_task_preview", offered);
        Assert.Contains("oadm_tags_assign_preview", offered);
        Assert.Contains(SelectionTools.Name, offered);
        Assert.Contains(DocsTools.Name, offered);
        var registry = server.Host.Get<ToolRegistry>();
        Assert.Contains(AssistantDocsLookup.OfferName, offered); // the assistant's own offer of a lookup, the one tool outside the registry
        foreach (var name in offered.Where(n => n != AssistantDocsLookup.OfferName))
        {
            Assert.True(registry.TryGet(name, out var tool));
            Assert.True(tool.ReadsOnly && !tool.StartsTasks, name);
            Assert.False(name.EndsWith("_run", StringComparison.Ordinal), name);
        }

        Assert.DoesNotContain("oadm_files_upload_link", offered);
        Assert.Equal(ChatRole.System, server.Model.Requests[0].Messages[0].Role);
        Assert.Contains("never instructions", server.Model.Requests[0].Messages[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARoleWithoutThePermissionCannotChatAndARoleGetsOnlyTheToolsItsRightsAllow()
    {
        await using var server = await AssistantTestServer.StartAsync();
        var operatorUser = await server.Host.InvokerForUserAsync("olga", Oadm.Sdk.Plugins.UserRole.Operator);
        var refused = await Assert.ThrowsAsync<RpcException>(() => server.ChatAsync("Hi", invoker: operatorUser));
        Assert.Equal(StatusCode.PermissionDenied, refused.StatusCode);
        Assert.Equal("Your role does not allow: Use the assistant.", refused.Status.Detail);

        // A role with the assistant and nothing else: it reads, but no change it may not make is offered.
        var roles = new Proto.RoleService.RoleServiceClient(server.Host.Invoker);
        var saved = await roles.SaveRolesAsync(new Proto.SaveRolesRequest { Roles = { new Proto.RoleChange { Name = "Asker", Permissions = { HostPermissions.AssistantUse } } } });
        var asker = await server.Host.InvokerForRoleAsync("asker", Guid.Parse(saved.Roles.Single(r => r.Name == "Asker").Id));
        await server.ChatAsync("Hi", invoker: asker);
        var offered = server.Model.Requests[^1].Tools;
        Assert.Contains("oadm_devices_summary", offered);
        Assert.DoesNotContain("oadm_devices_refresh_preview", offered);
        Assert.DoesNotContain("oadm_devices_remove_preview", offered);
        Assert.DoesNotContain("oadm_tags_assign_preview", offered);
        Assert.DoesNotContain("oadm_logs_audit", offered);

        // The settings are read by every role (the title bar button), without the key.
        var state = await server.Service(operatorUser).GetSettingsAsync(new Proto.Empty());
        Assert.True(state.Settings.Enabled);
        Assert.Empty(state.Settings.ApiKey);
        var set = await Assert.ThrowsAsync<RpcException>(async () => await server.Service(operatorUser).SetSettingsAsync(AssistantTestServer.Settings()));
        Assert.Equal(StatusCode.PermissionDenied, set.StatusCode);
    }

    [Fact]
    public async Task AGroupUserSeesOnlyTheGroupsDevicesAndTheSelectionIsCutToTheGroup()
    {
        await using var server = await AssistantTestServer.StartAsync();
        var lobby = await GroupCallers.DeviceAsync(server.Host, 31, "lobby");
        var lobby2 = await GroupCallers.DeviceAsync(server.Host, 32, "lobby");
        var vault = await GroupCallers.DeviceAsync(server.Host, 33, "vault");
        var user = await GroupCallers.ForTagAsync(server.Host, "lobbyuser", "lobby");

        server.Model
            .Call(("oadm_devices_search", null), (SelectionTools.Name, null), ("oadm_devices_summary", AssistantTestServer.Args("""{"filter":{"selection":true}}""")))
            .Say("Two devices.");
        var events = await server.ChatAsync("Which devices?", invoker: user.Invoker, selected: [lobby.Id.ToString(), vault.Id.ToString(), "not-an-id"]);

        var second = server.Model.Requests[1];
        var results = second.Results().Values.ToList();
        Assert.Equal(2, results[0]["total"]!.GetValue<int>()); // the search: the group's devices only
        Assert.DoesNotContain(vault.Id.ToString(), second.AllText(), StringComparison.Ordinal);
        Assert.Equal(1, results[1]["total"]!.GetValue<int>()); // the selection: the vault camera is not the user's
        Assert.Equal(lobby.Id.ToString(), results[1]["devices"]![0]!["id"]!.GetValue<string>());
        Assert.Equal(1, results[2]["total"]!.GetValue<int>()); // "selection": true in a filter
        Assert.Contains("\"selected_devices\":1", server.Model.Requests[0].AllText(), StringComparison.Ordinal);

        // Device lists become tables with every id; the tool lines show what the assistant does.
        var tables = events.Where(e => Kind(e) == Proto.AssistantEvent.KindOneofCase.Table).Select(e => e.Table).ToList();
        Assert.Equal(2, tables.Count);
        Assert.Equal(new[] { lobby.Id.ToString(), lobby2.Id.ToString() }.Order(StringComparer.Ordinal), tables[0].AllDeviceIds.Order(StringComparer.Ordinal));
        Assert.Equal(2, tables[0].Total);
        Assert.Equal([lobby.Id.ToString()], tables[1].AllDeviceIds);
        Assert.All(events.Where(e => Kind(e) == Proto.AssistantEvent.KindOneofCase.Tool), e => Assert.Equal("Looking at devices", e.Tool.Text));
    }

    [Fact]
    public async Task ASelectionFilterWithoutASelectionIsRefusedAndNeverMeansEveryDevice()
    {
        await using var server = await AssistantTestServer.StartAsync();
        await GroupCallers.DeviceAsync(server.Host, 41);
        server.Model.Call(("oadm_devices_remove_preview", AssistantTestServer.Args("""{"filter":{"selection":true}}"""))).Say("Nothing selected.");

        var events = await server.ChatAsync("Remove these devices");

        var result = server.Model.Requests[1].Results().Values.Single();
        Assert.Equal(SelectionTools.NothingSelected, result["error"]!.GetValue<string>());
        Assert.DoesNotContain(events, e => Kind(e) == Proto.AssistantEvent.KindOneofCase.Preview);
        Assert.Contains(events, e => Kind(e) == Proto.AssistantEvent.KindOneofCase.Tool && e.Tool.Failed);
    }

    [Fact]
    public async Task APreviewBecomesACardAndOnlyTheUsersOwnClickRunsItOnce()
    {
        await using var server = await AssistantTestServer.StartAsync();
        var device = await GroupCallers.DeviceAsync(server.Host, 51);
        server.Model.Call(("oadm_tags_assign_preview", AssistantTestServer.Args("""{"filter":{"selection":true},"add":["Lobby"]}"""))).Say("Press Run.");

        var events = await server.ChatAsync("Tag these Lobby", selected: [device.Id.ToString()]);

        var card = Assert.Single(events, e => Kind(e) == Proto.AssistantEvent.KindOneofCase.Preview).Preview;
        Assert.Equal("Change tags", card.Title);
        Assert.Equal(1, card.DeviceCount);
        Assert.Equal("1 device", card.CountText);
        Assert.Contains("Adds tags: Lobby", card.Details);
        Assert.False(card.Destructive);
        Assert.NotEmpty(card.ExpiresUtc);
        var toModel = server.Model.Requests[1].Results().Values.Single();
        Assert.Null(toModel["handle"]); // the model never holds the handle
        Assert.Equal(AssistantChats.PreviewNote, toModel["note"]!.GetValue<string>());
        Assert.DoesNotContain(card.Handle, server.Model.Requests[1].AllText(), StringComparison.Ordinal);

        // Another user, another session of the same user and another chat get NOT_FOUND; the handle stays for its own chat.
        var other = await server.Host.InvokerForUserAsync("other-admin", Oadm.Sdk.Plugins.UserRole.Admin);
        await AssertEndedAsync(server.Service(other).RunAsync(new Proto.AssistantRunRequest { ConversationId = "chat-1", Handle = card.Handle }));
        var secondSession = server.Host.InvokerFor(await InProcessAccess_TokenAsync(server.Host, TestServerHost.AdminUserName));
        await AssertEndedAsync(server.Service(secondSession).RunAsync(new Proto.AssistantRunRequest { ConversationId = "chat-1", Handle = card.Handle }));
        await AssertEndedAsync(server.Service().RunAsync(new Proto.AssistantRunRequest { ConversationId = "chat-2", Handle = card.Handle }));
        await AssertEndedAsync(server.Service().RunAsync(new Proto.AssistantRunRequest { ConversationId = "chat-1", Handle = "made-up" }));

        var run = await server.Service().RunAsync(new Proto.AssistantRunRequest { ConversationId = "chat-1", Handle = card.Handle });
        Assert.Equal(0, run.TaskCount);
        var tags = (await server.Host.Devices.ListAsync(new Proto.Empty())).Devices.Single(d => d.Id == device.Id.ToString()).Tags;
        Assert.Contains("Lobby", tags);
        await AssertEndedAsync(server.Service().RunAsync(new Proto.AssistantRunRequest { ConversationId = "chat-1", Handle = card.Handle })); // once

        // The model hears of the run with the next message, as data.
        await server.ChatAsync("Done?");
        Assert.Contains("The user pressed Run on a preview of oadm_tags_assign_run", server.Model.Requests[^1].AllText(), StringComparison.Ordinal);
    }

    private static Task<string> InProcessAccess_TokenAsync(TestServerHost host, string userName) =>
        Oadm.Server.Auth.InProcessAccess.CreateTokenAsync(host.Services, userName);

    private static async Task AssertEndedAsync(AsyncUnaryCall<Proto.AssistantRunReply> call)
    {
        var refused = await Assert.ThrowsAsync<RpcException>(async () => await call);
        Assert.Equal(StatusCode.NotFound, refused.StatusCode);
        Assert.Equal(AssistantTexts.PreviewEnded, refused.Status.Detail);
    }

    [Fact]
    public async Task ATaskRunOfAPreviewAnswersItsTaskIds()
    {
        await using var server = await AssistantTestServer.StartAsync();
        Assert.True(server.Host.Get<Oadm.Core.Plugins.PluginRegistry>().RegisterTaskPlugin(
            new Oadm.Plugins.Restart.RestartTaskPlugin(TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(2), TimeProvider.System),
            new Oadm.Core.Plugins.PluginOrigin("oadm.restart", "1.0.0", null)));
        var device = await GroupCallers.DeviceAsync(server.Host, 61);
        server.Model.Call(("oadm_task_preview", AssistantTestServer.Args($$$"""{"task":"oadm.restart","filter":{"ids":["{{{device.Id}}}"]},"input":{}}"""))).Say("Press Run.");

        var events = await server.ChatAsync("Restart it");

        var card = Assert.Single(events, e => Kind(e) == Proto.AssistantEvent.KindOneofCase.Preview).Preview;
        Assert.Equal(1, card.DeviceCount);
        var run = await server.Service().RunAsync(new Proto.AssistantRunRequest { ConversationId = "chat-1", Handle = card.Handle });
        Assert.Equal(1, run.TaskCount);
        var taskId = Assert.Single(run.TaskIds);
        Assert.NotEmpty(run.BatchId);
        var tasks = await server.Host.Tasks.ListAsync(new Proto.ListTasksRequest());
        Assert.Contains(tasks.Tasks, t => t.Id == taskId && t.Owner.StartsWith(TestServerHost.AdminUserName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TryTheFailedDevicesAgainPreviewsTheSameChangeForThemOnlyAsANewCard()
    {
        await using var server = await AssistantTestServer.StartAsync();
        var lobby = await GroupCallers.DeviceAsync(server.Host, 71, "lobby");
        var lobby2 = await GroupCallers.DeviceAsync(server.Host, 72, "lobby");
        var vault = await GroupCallers.DeviceAsync(server.Host, 73, "vault");
        server.Model.Call(("oadm_devices_refresh_preview", AssistantTestServer.Args("""{"filter":{"selection":true}}"""))).Say("Press Run.");
        var events = await server.ChatAsync("Refresh these", selected: [lobby.Id.ToString(), lobby2.Id.ToString()]);
        var card = Assert.Single(events, e => Kind(e) == Proto.AssistantEvent.KindOneofCase.Preview).Preview;
        Assert.Equal(2, card.DeviceCount);

        var retry = new Proto.AssistantRetryRequest { ConversationId = "chat-1", Handle = card.Handle, FailedDeviceIds = { lobby2.Id.ToString() } };
        var notRun = await Assert.ThrowsAsync<RpcException>(async () => await server.Service().RetryFailedAsync(retry));
        Assert.Equal(StatusCode.NotFound, notRun.StatusCode); // only a card that ran

        await server.Service().RunAsync(new Proto.AssistantRunRequest { ConversationId = "chat-1", Handle = card.Handle });
        var again = await server.Service().RetryFailedAsync(retry);
        Assert.Equal(1, again.DeviceCount);
        Assert.Equal(card.Title, again.Title);
        Assert.NotEqual(card.Handle, again.Handle);
        await server.Service().RunAsync(new Proto.AssistantRunRequest { ConversationId = "chat-1", Handle = again.Handle }); // a card like any other

        // Another chat or user gets NOT_FOUND; a device of the request outside the group is dropped.
        var otherChat = await Assert.ThrowsAsync<RpcException>(async () => await server.Service().RetryFailedAsync(
            new Proto.AssistantRetryRequest { ConversationId = "chat-2", Handle = card.Handle, FailedDeviceIds = { lobby.Id.ToString() } }));
        Assert.Equal(StatusCode.NotFound, otherChat.StatusCode);
        var user = await GroupCallers.ForTagAsync(server.Host, "lobbyuser", "lobby");
        server.Model.Call(("oadm_devices_refresh_preview", AssistantTestServer.Args("""{"filter":{"selection":true}}"""))).Say("Press Run.");
        var mine = (await server.ChatAsync("Refresh it", conversation: "chat-u", invoker: user.Invoker, selected: [lobby.Id.ToString()]))
            .Single(e => Kind(e) == Proto.AssistantEvent.KindOneofCase.Preview).Preview;
        await server.Service(user.Invoker).RunAsync(new Proto.AssistantRunRequest { ConversationId = "chat-u", Handle = mine.Handle });
        var cut = await server.Service(user.Invoker).RetryFailedAsync(
            new Proto.AssistantRetryRequest { ConversationId = "chat-u", Handle = mine.Handle, FailedDeviceIds = { lobby.Id.ToString(), vault.Id.ToString() } });
        Assert.Equal(1, cut.DeviceCount);
        var none = await Assert.ThrowsAsync<RpcException>(async () => await server.Service(user.Invoker).RetryFailedAsync(
            new Proto.AssistantRetryRequest { ConversationId = "chat-u", Handle = mine.Handle, FailedDeviceIds = { vault.Id.ToString() } }));
        Assert.Equal((StatusCode.FailedPrecondition, AssistantChats.NoDevicesToRetry), (none.StatusCode, none.Status.Detail));
    }

    [Fact]
    public async Task DevicesChosenWithTheAtSignReachTheModelCutToTheGroup()
    {
        await using var server = await AssistantTestServer.StartAsync();
        var lobby = await GroupCallers.DeviceAsync(server.Host, 81, "lobby");
        var vault = await GroupCallers.DeviceAsync(server.Host, 82, "vault");
        var user = await GroupCallers.ForTagAsync(server.Host, "lobbyuser", "lobby");
        server.Model.Say("That one.");
        var message = new Proto.AssistantMessage { ConversationId = "chat-m", Text = "What about this camera?", Model = "test-model", MentionedDeviceIds = { lobby.Id.ToString(), vault.Id.ToString() } };
        using (var call = server.Service(user.Invoker).Chat(message))
        {
            await foreach (var _ in call.ResponseStream.ReadAllAsync())
            {
            }
        }

        var data = server.Model.Requests[^1].AllText();
        Assert.Contains("\"devices_the_user_chose\":{\"count\":1", data, StringComparison.Ordinal);
        Assert.Contains(lobby.Id.ToString(), data, StringComparison.Ordinal);
        Assert.DoesNotContain(vault.Id.ToString(), data, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APictureIsShownAlwaysAndReachesTheModelOnlyWithTheSetting(bool sendPictures)
    {
        var camera = new PictureCamera();
        await using var server = await AssistantTestServer.StartAsync(sendPictures: sendPictures, configureServices: services =>
        {
            services.RemoveAll<ILiveVideoSourceFactory>();
            services.AddSingleton<ILiveVideoSourceFactory>(camera);
        });
        var device = await GroupCallers.DeviceAsync(server.Host, 71);
        server.Model.Call(("oadm_devices_snapshot", AssistantTestServer.Args($$"""{"device_id":"{{device.Id}}"}"""))).Say("A picture.");

        var events = await server.ChatAsync("Show me a picture");

        var picture = Assert.Single(events, e => Kind(e) == Proto.AssistantEvent.KindOneofCase.Picture).Picture;
        Assert.Equal(device.Id.ToString(), picture.DeviceId);
        Assert.Equal(PictureCamera.Jpeg, picture.Jpeg.ToByteArray());
        var second = server.Model.Requests[1];
        var images = second.Messages.SelectMany(m => m.Contents).OfType<DataContent>().ToList();
        var note = second.Results().Values.Single()["note"]!.GetValue<string>();
        if (sendPictures)
        {
            Assert.Equal(PictureCamera.Jpeg, Assert.Single(images).Data.ToArray());
            Assert.Equal(AssistantChats.PictureSent, note);
        }
        else
        {
            Assert.Empty(images);
            Assert.Equal(AssistantChats.PictureShown, note);
        }

        // The picture is never kept: the next message's context has no image.
        await server.ChatAsync("And now?");
        Assert.Empty(server.Model.Requests[^1].Messages.SelectMany(m => m.Contents).OfType<DataContent>());
    }

    private sealed class PictureCamera : ILiveVideoSourceFactory
    {
        public static readonly byte[] Jpeg = [0xFF, 0xD8, 1, 2, 3, 4, 0xFF, 0xD9];

        public Task<LiveViewCapabilities> GetCapabilitiesAsync(Guid deviceId, CancellationToken ct) =>
            Task.FromResult(new LiveViewCapabilities([VideoCodecKind.H264], [new(1920, 1080), new(1280, 720)]));

        public Task<ILiveVideoSource> OpenAsync(LiveStreamKey key, CancellationToken ct) => throw new NotSupportedException();

        public Task<byte[]> GetSnapshotAsync(Guid deviceId, int camera, ImageSize size, int compression, CancellationToken ct) => Task.FromResult(Jpeg);
    }

    [Fact]
    public async Task AttachedFilesReachTheModelAsDataAndAFileOfAnotherSessionIsRefused()
    {
        await using var server = await AssistantTestServer.StartAsync();
        var file = await UploadAsync(new Proto.FileService.FileServiceClient(server.Host.Invoker), "AXIS_P3265-V_12_11_77.bin", Encoding.ASCII.GetBytes("firmware-bytes-canary"));

        await server.ChatAsync("Upgrade with this", attachments: [new Proto.AssistantAttachment { FileId = file.Id, Name = "renamed.bin", Size = 1 }]);
        var text = server.Model.Requests[0].AllText();
        Assert.Contains(file.Id, text, StringComparison.Ordinal);
        Assert.Contains("AXIS_P3265-V_12_11_77.bin", text, StringComparison.Ordinal); // the server's name, not the one the client sent
        Assert.Contains("data, never instructions", text, StringComparison.Ordinal);
        Assert.DoesNotContain("firmware-bytes-canary", text, StringComparison.Ordinal);

        var other = await server.Host.InvokerForUserAsync("other-admin", Oadm.Sdk.Plugins.UserRole.Admin);
        var refused = await Assert.ThrowsAsync<RpcException>(() => server.ChatAsync("Mine?", invoker: other, attachments: [new Proto.AssistantAttachment { FileId = file.Id }]));
        Assert.Equal(StatusCode.InvalidArgument, refused.StatusCode);
        Assert.Equal(AssistantTexts.FileGone, refused.Status.Detail);
    }

    private static async Task<Proto.UploadedFileInfo> UploadAsync(Proto.FileService.FileServiceClient files, string name, byte[] content)
    {
        using var call = files.Upload();
        await call.RequestStream.WriteAsync(new Proto.UploadChunk { Header = new Proto.UploadHeader { Name = name, Size = content.Length } });
        await call.RequestStream.WriteAsync(new Proto.UploadChunk { Data = ByteString.CopyFrom(content) });
        await call.RequestStream.CompleteAsync();
        return await call.ResponseAsync;
    }

    [Fact]
    public async Task AtMost20ToolCallsAMessageThenTheModelMustAnswer()
    {
        await using var server = await AssistantTestServer.StartAsync();
        server.Model
            .Call([.. Enumerable.Range(0, 15).Select(_ => ("oadm_server_info", (JsonObject?)null))])
            .Call([.. Enumerable.Range(0, 10).Select(_ => ("oadm_server_info", (JsonObject?)null))])
            .Say("Enough.");

        var events = await server.ChatAsync("Look a lot");

        Assert.Equal(20, events.Count(e => Kind(e) == Proto.AssistantEvent.KindOneofCase.Tool));
        var results = server.Model.Requests[2].Results().Values.ToList();
        Assert.Equal(5, results.Count(r => r["error"]?.GetValue<string>() == AssistantTexts.ToolLimit));
        Assert.Equal(ChatToolMode.None, server.Model.Requests[2].Options!.ToolMode);
        Assert.Equal(server.Model.Requests[0].Tools, server.Model.Requests[2].Tools); // declared, never dropped
        Assert.Equal("Enough.", AssistantTestServer.TextOf(events));
    }

    [Fact]
    public async Task StepsPerAnswerAndAnswerTimeAreTheCardsSettingsAndAnOlderClientKeepsTheDefaults()
    {
        await using var server = await AssistantTestServer.StartAsync();
        var settings = AssistantTestServer.Settings();
        settings.StepsPerAnswer = 3;
        settings.AnswerSeconds = 300;
        var state = await server.SetAsync(settings);
        Assert.Equal((3, 300), (state.Settings.StepsPerAnswer, state.Settings.AnswerSeconds));

        server.Model
            .Call([.. Enumerable.Range(0, 5).Select(_ => ("oadm_server_info", (JsonObject?)null))])
            .Say("Enough.");
        var events = await server.ChatAsync("Look a lot");
        Assert.Equal(3, events.Count(e => Kind(e) == Proto.AssistantEvent.KindOneofCase.Tool));
        Assert.Equal(2, server.Model.Requests[1].Results().Values.Count(r => r["error"]?.GetValue<string>() == AssistantTexts.ToolLimit));

        // A client without the two fields sends 0: the defaults, never a refusal.
        state = await server.SetAsync(AssistantTestServer.Settings());
        Assert.Equal((AssistantTexts.MaxToolCallsPerMessage, AssistantTexts.DefaultAnswerSeconds), (state.Settings.StepsPerAnswer, state.Settings.AnswerSeconds));
    }

    [Fact]
    public async Task TheMessageIsCheckedAndASecondMessageWaitsUntilStopEndsTheFirst()
    {
        await using var server = await AssistantTestServer.StartAsync();
        var service = server.Service();
        await AssertRefusedAsync(() => server.ChatAsync(" "), StatusCode.InvalidArgument, AssistantTexts.EmptyMessage);
        await AssertRefusedAsync(() => server.ChatAsync(new string('a', 8_001)), StatusCode.InvalidArgument, AssistantTexts.MessageTooLong);
        await AssertRefusedAsync(() => server.ChatAsync("Hi", selected: Enumerable.Range(0, 10_001).Select(_ => Guid.NewGuid().ToString())), StatusCode.InvalidArgument, AssistantTexts.TooManySelected);

        server.Model.Hang();
        using var first = service.Chat(new Proto.AssistantMessage { ConversationId = "chat-1", Text = "Take long", Model = "test-model" });
        Assert.True(await first.ResponseStream.MoveNext());
        Assert.Equal("Working", first.ResponseStream.Current.Text);
        await AssertRefusedAsync(() => server.ChatAsync("Again"), StatusCode.FailedPrecondition, AssistantTexts.Busy);

        await service.StopAsync(new Proto.AssistantConversationRequest { ConversationId = "chat-1" });
        Assert.True(await first.ResponseStream.MoveNext());
        Assert.True(first.ResponseStream.Current.Done.Stopped);
        Assert.False(await first.ResponseStream.MoveNext());

        // The chat stays: the next message has the stopped one in its context.
        server.Model.Say("Here.");
        await server.ChatAsync("Now");
        Assert.Contains("Take long", server.Model.Requests[^1].AllText(), StringComparison.Ordinal);
    }

    private static async Task AssertRefusedAsync(Func<Task> call, StatusCode code, string detail)
    {
        var refused = await Assert.ThrowsAsync<RpcException>(call);
        Assert.Equal(code, refused.StatusCode);
        Assert.Equal(detail, refused.Status.Detail);
    }

    [Fact]
    public async Task EndAndSignOutForgetTheChatAndItsPreviews()
    {
        await using var server = await AssistantTestServer.StartAsync();
        var device = await GroupCallers.DeviceAsync(server.Host, 81);
        var token = await InProcessAccess_TokenAsync(server.Host, "admin2");
        var invoker = server.Host.InvokerFor(token);
        server.Model.Call(("oadm_devices_refresh_preview", AssistantTestServer.Args($$$"""{"filter":{"ids":["{{{device.Id}}}"]}}"""))).Say("Run it.");
        var card = Assert.Single(await server.ChatAsync("Refresh", invoker: invoker), e => Kind(e) == Proto.AssistantEvent.KindOneofCase.Preview).Preview;
        await server.Service(invoker).EndAsync(new Proto.AssistantConversationRequest { ConversationId = "chat-1" });
        await AssertEndedAsync(server.Service(invoker).RunAsync(new Proto.AssistantRunRequest { ConversationId = "chat-1", Handle = card.Handle }));

        // A new chat: the context starts empty.
        server.Model.Say("New.");
        await server.ChatAsync("First of the new one", conversation: "chat-2", invoker: invoker);
        Assert.DoesNotContain("Refresh", server.Model.Requests[^1].Messages.Where(m => m.Role == ChatRole.User).Select(m => m.Text));

        Assert.Equal(1, server.Host.Get<AssistantConversations>().Count);
        await new Proto.AuthService.AuthServiceClient(invoker).LogoutAsync(new Proto.Empty());
        Assert.Equal(0, server.Host.Get<AssistantConversations>().Count);
    }

    [Fact]
    public async Task NoNoticeOfWhereTheDataGoesInTheChatForAnyProvider()
    {
        // Decision 2026-11-16: the panel shows no "Your messages ... go to Anthropic." line; the user docs say where the data goes.
        await using var server = await AssistantTestServer.StartAsync();
        Assert.DoesNotContain(await server.ChatAsync("One"), e => Kind(e) == Proto.AssistantEvent.KindOneofCase.Notice);
        Assert.DoesNotContain(await server.ChatAsync("Two"), e => Kind(e) == Proto.AssistantEvent.KindOneofCase.Notice);

        await server.SetAsync(AssistantTestServer.Settings(AssistantTexts.Ollama));
        Assert.DoesNotContain(await server.ChatAsync("Three", conversation: "chat-3"), e => Kind(e) == Proto.AssistantEvent.KindOneofCase.Notice);
        Assert.Equal("http://10.0.0.5:11434", server.Providers.Connections[^1].Address);
    }

    [Fact]
    public async Task EveryMessageNamesItsModelAndAnotherModelKeepsTheChat()
    {
        await using var server = await AssistantTestServer.StartAsync();
        var refused = await Assert.ThrowsAsync<RpcException>(() => server.ChatAsync("Hi", model: " "));
        Assert.Equal(StatusCode.InvalidArgument, refused.StatusCode);
        Assert.Equal(AssistantTexts.ChooseModel, refused.Status.Detail);
        Assert.Equal("model", refused.Trailers.GetValue(AssistantTexts.FieldTrailer));

        server.Model.Say("First.").Say("Second.");
        await server.ChatAsync("Remember lobby", model: "model-a");
        await server.ChatAsync("And now?", model: "model-b");
        Assert.Equal(["model-a", "model-b"], server.Providers.Connections.Select(c => c.Model));
        Assert.Contains("Remember lobby", server.Model.Requests[^1].AllText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplaceLastDropsTheLastExchangeAndNeedsOne()
    {
        await using var server = await AssistantTestServer.StartAsync();
        var refused = await Assert.ThrowsAsync<RpcException>(() => server.ChatAsync("Hi", replaceLast: true));
        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
        Assert.Equal(AssistantTexts.NothingToRetry, refused.Status.Detail);

        server.Model.Say("About the lobby.").Say("About the yard.").Say("Yard again.").Say("Gate.");
        await server.ChatAsync("Remember lobby");
        await server.ChatAsync("Remember yard");

        // Try again: the same text, the old answer gone from what the model sees.
        await server.ChatAsync("Remember yard", replaceLast: true);
        var retried = server.Model.Requests[^1].AllText();
        Assert.Contains("Remember lobby", retried, StringComparison.Ordinal);
        Assert.DoesNotContain("About the yard.", retried, StringComparison.Ordinal);
        Assert.Single(server.Model.Requests[^1].Messages, m => m.Role == ChatRole.User && m.Text.Contains("Remember yard", StringComparison.Ordinal));

        // Edit: another text takes the place of the last message.
        await server.ChatAsync("Remember gate", replaceLast: true);
        var edited = server.Model.Requests[^1].AllText();
        Assert.Contains("Remember lobby", edited, StringComparison.Ordinal);
        Assert.Contains("About the lobby.", edited, StringComparison.Ordinal);
        Assert.DoesNotContain("Remember yard", edited, StringComparison.Ordinal);
        Assert.DoesNotContain("Yard again.", edited, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AModelThatRefusesToolsSaysSoOnceAsTheAnswersError()
    {
        await using var server = await AssistantTestServer.StartAsync(provider: AssistantTexts.Ollama);
        server.Model.Then((_, _) => throw new HttpRequestException("registry.ollama.ai/library/llama2:latest does not support tools", null, System.Net.HttpStatusCode.BadRequest));

        var events = await server.ChatAsync("Hi", model: "llama2");

        Assert.Equal(AssistantTexts.CannotUseTools, events[^1].Done.Error);
        Assert.DoesNotContain(events, e => Kind(e) == Proto.AssistantEvent.KindOneofCase.Notice || Kind(e) == Proto.AssistantEvent.KindOneofCase.Text);
        Assert.False(AssistantProviderErrors.RejectsTools(new HttpRequestException("tools not supported", null, System.Net.HttpStatusCode.Unauthorized)));
    }

    [Fact]
    public async Task TheModelListNeedsThePermissionAndSuggestsAModel()
    {
        await using var server = await AssistantTestServer.StartAsync();
        server.Providers.Models = ["claude-haiku-5", AssistantTexts.AnthropicDefaultModel];
        var list = await server.Service().ListModelsAsync(new Proto.Empty());
        Assert.Equal(["claude-haiku-5", AssistantTexts.AnthropicDefaultModel], list.Models);
        Assert.False(list.Editable);
        Assert.Equal(AssistantTexts.AnthropicDefaultModel, list.Suggested);

        server.Providers.Models = [];
        var none = await server.Service().ListModelsAsync(new Proto.Empty());
        Assert.True(none.Editable);
        Assert.Empty(none.Models);

        var operatorUser = await server.Host.InvokerForUserAsync("olga", Oadm.Sdk.Plugins.UserRole.Operator);
        await AssertRefusedAsync(async () => await server.Service(operatorUser).ListModelsAsync(new Proto.Empty()), StatusCode.PermissionDenied, "Your role does not allow: Use the assistant.");
    }

    [Fact]
    public async Task WhileOffEveryChatIsRefused()
    {
        await using var server = await AssistantTestServer.StartAsync(turnOn: false);
        await AssertRefusedAsync(() => server.ChatAsync("Hi"), StatusCode.FailedPrecondition, AssistantTexts.IsOff);
        Assert.Empty(server.Model.Requests);
        Assert.Equal(AssistantTexts.Off, (await server.Service().GetSettingsAsync(new Proto.Empty())).Status);
    }

    [Fact]
    public async Task AProviderErrorEndsTheAnswerInPlainWordsWithoutTheKey()
    {
        await using var server = await AssistantTestServer.StartAsync();
        server.Model.Then((_, _) => throw new HttpRequestException("Unauthorized " + AssistantTestServer.ApiKey, null, System.Net.HttpStatusCode.Unauthorized));

        var events = await server.ChatAsync("Hi");

        Assert.Equal(AssistantProviderErrors.KeyRefused, events[^1].Done.Error);
        Assert.Equal(AssistantTestServer.ApiKey, server.Providers.Connections[^1].ApiKey); // the key reaches the provider, and only there
        Assert.DoesNotContain(AssistantTestServer.ApiKey, server.Providers.Connections[^1].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoSecretInAnyAnswerSettingAuditEntryOrLogLine()
    {
        await using var server = await AssistantTestServer.StartAsync();
        const string DevicePassword = "canary-device-a51c";
        var device = await DeviceServiceTests.AddDeviceAsync(server.Host, "10.12.0.1", 9101, password: DevicePassword);
        server.Model
            .Call(("oadm_devices_get", AssistantTestServer.Args($$"""{"device_id":"{{device.Id}}"}""")), ("oadm_server_info", null), ("oadm_settings_get", null),
                ("oadm_devices_credentials_preview", AssistantTestServer.Args("""{"filter":{}}""")))
            .Then((_, _) => throw new InvalidOperationException("boom " + AssistantTestServer.ApiKey))
            .Say("Ok.");
        var events = new StringBuilder();
        foreach (var e in await server.ChatAsync("Tell me everything"))
        {
            events.Append(e.ToString());
        }

        foreach (var e in await server.ChatAsync("Again"))
        {
            events.Append(e.ToString());
        }

        var state = await server.Service().GetSettingsAsync(new Proto.Empty());
        Assert.True(state.HasApiKey);
        var stored = await server.Host.Get<Oadm.Core.Settings.ServerSettingsStore>().GetAsync<AssistantSettings>(AssistantSettings.Key, CancellationToken.None);
        var audit = string.Join("\n", (await server.Host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries.Select(e => e.ToString()));
        Assert.Contains(AuditActions.AssistantChanged, audit, StringComparison.Ordinal);
        var logs = ReadLogs(server.Host.Get<OadmPaths>().LogsDirectory);
        var modelSaw = string.Join("\n", server.Model.Requests.Select(r => r.AllText()));
        foreach (var secret in new[] { AssistantTestServer.ApiKey, DevicePassword, server.Host.AdminToken })
        {
            Assert.DoesNotContain(secret, events.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, state.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, stored!.ProtectedApiKey!, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, audit, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, logs, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, modelSaw, StringComparison.Ordinal);
        }
    }

    private static string ReadLogs(string directory)
    {
        var text = new StringBuilder();
        foreach (var file in Directory.Exists(directory) ? Directory.GetFiles(directory) : [])
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            text.Append(reader.ReadToEnd());
        }

        return text.ToString();
    }

    [Fact]
    public async Task AtTheDailyLimitTheChatIsRefused()
    {
        await using var server = await AssistantTestServer.StartAsync(dailyLimit: 1_000);
        server.Model.TokensPerAnswer = 1_200;
        await server.ChatAsync("Expensive");
        await AssertRefusedAsync(() => server.ChatAsync("More"), StatusCode.ResourceExhausted, AssistantTexts.DailyLimit);
        Assert.Equal(1_200, (await server.Service().GetSettingsAsync(new Proto.Empty())).UsedToday);
    }
}
