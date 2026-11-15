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
public sealed class AssistantGrpcService(AssistantChats chats, AuditLog audit) : Proto.AssistantService.AssistantServiceBase
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
        chats.RunAsync(AuthInterceptor.CallerOf(context), request.ConversationId, request.Handle, context.CancellationToken);

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

        var protectedKey = key.Length > 0 ? AssistantSettings.Protect(chats.Protector, key)
            : saved.Provider == provider ? saved.ProtectedApiKey : null;
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

        return new AssistantSettings
        {
            Enabled = request.Enabled,
            Provider = provider,
            Address = address,
            ProtectedApiKey = protectedKey,
            SendPictures = request.SendPictures,
            DailyLimit = request.DailyLimit,
            UserDailyLimit = request.UserDailyLimit,
        };
    }

    private static RpcException Field(string field, string message) =>
        new(new Status(StatusCode.InvalidArgument, message), new Metadata { { AssistantTexts.FieldTrailer, field } });
}
