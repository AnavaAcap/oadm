using Grpc.Core;

using Oadm.Contracts.Assistant;
using Oadm.Core.Auth;
using Oadm.Server.Auth;
using Oadm.Server.Common;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Assistant;

/// <summary>
/// gRPC AssistantService (docs/spec/assistant.md): the chat of the client's panel (Chat, Stop, Run, End: the permission "Use the
/// assistant") and the Settings card AI, section Assistant (Get for every signed-in user without the key; Set and Test for
/// administrators, Set audited as "Changed assistant").
/// </summary>
public sealed class AssistantGrpcService(AssistantChats chats, AuditLog audit, IServiceProvider services) : Proto.AssistantService.AssistantServiceBase
{
    /// <summary>The longest Test takes.</summary>
    public static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);


    public override async Task Chat(Proto.AssistantMessage request, IServerStreamWriter<Proto.AssistantEvent> responseStream, ServerCallContext context)
    {
        var caller = AuthInterceptor.CallerOf(context);
        await chats.ChatAsync(request, caller, e => responseStream.WriteAsync(e, context.CancellationToken), context.CancellationToken).ConfigureAwait(false);
    }

    public override Task<Proto.Empty> Stop(Proto.AssistantConversationRequest request, ServerCallContext context)
    {
        chats.Stop(AuthInterceptor.CallerOf(context), request.ConversationId);
        return Task.FromResult(new Proto.Empty());
    }

    public override Task<Proto.AssistantRunReply> Run(Proto.AssistantRunRequest request, ServerCallContext context) =>
        chats.RunAsync(AuthInterceptor.CallerOf(context), request.ConversationId, request.Handle, context.CancellationToken, request.Secrets);

    /// <summary>A file a tool answer of the user's chat offered, made now with the user's rights; audited "Downloaded file".</summary>
    public override async Task Download(Proto.AssistantDownloadRequest request, IServerStreamWriter<Proto.DownloadChunk> responseStream, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(responseStream);
        var caller = AuthInterceptor.CallerOf(context);
        var file = services.GetService(typeof(Tools.ToolHandles)) is Tools.ToolHandles handles ? chats.OfferedFile(caller, request.ConversationId, request.FileRef, handles) : null;
        if (file is null)
        {
            throw GrpcGuard.NotFound(AssistantTexts.FileEnded);
        }

        var ct = context.CancellationToken;
        var content = await file.Open(ct).ConfigureAwait(false);
        long sent = 0;
        try
        {
            await using (content.ConfigureAwait(false))
            {
                context.WriteOptions = new WriteOptions(WriteFlags.NoCompress);
                await responseStream.WriteAsync(new Proto.DownloadChunk { FileName = file.Name, Length = file.Size ?? -1 }).ConfigureAwait(false);
                var buffer = new byte[Plugins.PluginGrpcService.DownloadChunkBytes];
                int read;
                while ((read = await content.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await responseStream.WriteAsync(new Proto.DownloadChunk { Data = Google.Protobuf.ByteString.CopyFrom(buffer, 0, read) }).ConfigureAwait(false);
                    sent += read;
                }
            }
        }
        finally
        {
            await audit.WriteAsync(AuditActions.FileDownloaded, file.Name,
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{sent:N0} bytes, from the assistant"), CancellationToken.None).ConfigureAwait(false);
        }
    }

    public override Task<Proto.AssistantPreviewCard> RetryFailed(Proto.AssistantRetryRequest request, ServerCallContext context) =>
        chats.RetryFailedAsync(AuthInterceptor.CallerOf(context), request, context.CancellationToken);

    public override Task<Proto.Empty> End(Proto.AssistantConversationRequest request, ServerCallContext context)
    {
        chats.End(AuthInterceptor.CallerOf(context), request.ConversationId);
        return Task.FromResult(new Proto.Empty());
    }

    public override Task<Proto.AssistantModelList> ListModels(Proto.Empty request, ServerCallContext context) =>
        chats.ListModelsAsync(context.CancellationToken);

    public override async Task<Proto.AssistantSettingsState> GetSettings(Proto.Empty request, ServerCallContext context)
    {
        var settings = await chats.SettingsAsync(context.CancellationToken).ConfigureAwait(false);
        return await StateAsync(settings).ConfigureAwait(false);
    }

    public override async Task<Proto.AssistantSettingsState> SetSettings(Proto.AssistantSettings request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var before = await chats.SettingsAsync(ct).ConfigureAwait(false);
        var after = Checked(request, before, requireAll: request.Enabled);
        await after.SaveAsync(chats.Store, ct).ConfigureAwait(false);
        await audit.WriteAsync(AuditActions.AssistantChanged, "Assistant", AssistantSettings.Changes(before, after), ct).ConfigureAwait(false);
        if (!after.Enabled || after.Provider != before.Provider || after.Address != before.Address)
        {
            chats.Conversations.EndAll(); // the chats of another provider start again (a user's other model keeps the chat)
        }

        return await StateAsync(after).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks the address and the key by reading the provider's model list (no model is chosen here). An OpenAI-compatible
    /// server without a list answered too (404): reachable, and its key is checked by the first message.
    /// </summary>
    public override async Task<Proto.AssistantTestReply> Test(Proto.AssistantSettings request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var saved = await chats.SettingsAsync(ct).ConfigureAwait(false);
        var settings = Checked(request, saved, requireAll: true);
        var apiKey = settings.ApiKey(chats.Protector);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TestTimeout);
            _ = await chats.Providers.ReadModelsAsync(new AssistantConnection(settings.Provider, settings.Address, string.Empty, apiKey), timeout.Token).ConfigureAwait(false);
            return new Proto.AssistantTestReply { Ok = true, Message = AssistantTexts.ProviderAnswered };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new Proto.AssistantTestReply { Ok = false, Message = AssistantTexts.NoAnswer };
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not RpcException)
        {
            return new Proto.AssistantTestReply { Ok = false, Message = AssistantProviderErrors.Plain(ex, apiKey) };
        }
    }

    private Task<Proto.AssistantSettingsState> StateAsync(AssistantSettings settings)
    {
        var state = new Proto.AssistantSettingsState
        {
            Settings = new Proto.AssistantSettings
            {
                Enabled = settings.Enabled,
                Provider = settings.Provider,
                Address = settings.Address ?? string.Empty,
                SendPictures = settings.SendPictures,
                DailyLimit = settings.DailyLimit,
                UserDailyLimit = settings.UserDailyLimit,
                StepsPerAnswer = settings.StepsPerAnswer,
                AnswerSeconds = settings.AnswerSeconds,
            },
            HasApiKey = settings.HasApiKey,
            UsedToday = chats.Usage.UsedToday(),
            Status = settings.Status,
        };
        return Task.FromResult(state);
    }

    /// <summary>
    /// The settings of <paramref name="request"/> on top of <paramref name="saved"/>: the saved key when none is given (dropped when
    /// the provider changes), field errors with the trailer <c>oadm-field</c>. <paramref name="requireAll"/>: Anthropic and OpenAI
    /// need a key (Turn on, Test).
    /// </summary>
    private AssistantSettings Checked(Proto.AssistantSettings request, AssistantSettings saved, bool requireAll)
    {
        var provider = request.Provider?.Trim() ?? string.Empty;
        if (!AssistantTexts.Providers.Contains(provider))
        {
            throw Field("provider", AssistantTexts.ChooseProvider);
        }

        string? address = null;
        if (AssistantSettings.NeedsAddress(provider))
        {
            address = request.Address?.Trim() ?? string.Empty;
            if (address.Length == 0)
            {
                throw Field("address", AssistantTexts.EnterAddress);
            }

            if (address.Length > 500 || !Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            {
                throw Field("address", AssistantTexts.AddressNotValid);
            }
        }

        var key = request.ApiKey?.Trim() ?? string.Empty;
        if (key.Length > 500)
        {
            throw Field("api_key", AssistantTexts.ApiKeyTooLong);
        }

        // The saved key goes only to the provider and address it was saved for: another one needs the key again.
        var sameTarget = saved.Provider == provider && string.Equals(saved.Address ?? string.Empty, address ?? string.Empty, StringComparison.Ordinal);
        if (key.Length == 0 && !sameTarget && saved.HasApiKey && AssistantSettings.NeedsApiKey(provider))
        {
            throw Field("api_key", AssistantTexts.EnterApiKey);
        }

        var protectedKey = key.Length > 0 ? AssistantSettings.Protect(chats.Protector, key)
            : sameTarget ? saved.ProtectedApiKey : null;
        if (!AssistantSettings.NeedsApiKey(provider) && key.Length == 0)
        {
            protectedKey = null; // Ollama takes no key
        }

        if (requireAll && provider is AssistantTexts.Anthropic or AssistantTexts.OpenAi && string.IsNullOrEmpty(protectedKey))
        {
            throw Field("api_key", AssistantTexts.EnterApiKey); // an OpenAI-compatible server may take none
        }

        if (request.DailyLimit is < AssistantTexts.MinDailyLimit or > AssistantTexts.MaxDailyLimit)
        {
            throw Field("daily_limit", AssistantTexts.DailyLimitRange);
        }

        if (request.UserDailyLimit != 0 && request.UserDailyLimit is < AssistantTexts.MinDailyLimit or > AssistantTexts.MaxDailyLimit)
        {
            throw Field("user_daily_limit", AssistantTexts.UserDailyLimitRange);
        }

        // 0: an older client without these fields keeps the default.
        var steps = request.StepsPerAnswer == 0 ? AssistantTexts.MaxToolCallsPerMessage : request.StepsPerAnswer;
        if (steps is < AssistantTexts.MinStepsPerAnswer or > AssistantTexts.MaxStepsPerAnswer)
        {
            throw Field("steps_per_answer", AssistantTexts.StepsRange);
        }

        var seconds = request.AnswerSeconds == 0 ? AssistantTexts.DefaultAnswerSeconds : request.AnswerSeconds;
        if (seconds is < AssistantTexts.MinAnswerSeconds or > AssistantTexts.MaxAnswerSeconds)
        {
            throw Field("answer_seconds", AssistantTexts.AnswerSecondsRange);
        }

        return new AssistantSettings
        {
            Enabled = request.Enabled,
            Provider = provider,
            Address = address,
            ProtectedApiKey = protectedKey,
            SendPictures = request.SendPictures,
            DailyLimit = request.DailyLimit,
            UserDailyLimit = request.UserDailyLimit,
            StepsPerAnswer = steps,
            AnswerSeconds = seconds,
        };
    }

    private static RpcException Field(string field, string message) =>
        new(new Status(StatusCode.InvalidArgument, message), new Metadata { { AssistantTexts.FieldTrailer, field } });
}
