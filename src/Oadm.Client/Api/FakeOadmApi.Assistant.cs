using System.Runtime.CompilerServices;

using Google.Protobuf;

using Grpc.Core;

using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>
/// Fake AssistantService (docs/spec/assistant.md) so <c>--fake</c> shows the Assistant panel and the Settings card AI, section
/// Assistant, and view models are testable without a server: a scripted answer per message (<see cref="AssistantScript"/>,
/// a default one by keywords: offline devices as a table, a picture, a restart as a preview card, otherwise Markdown), the
/// model list (<see cref="AssistantModels"/>), the model of every message (empty: "Choose a model.", "no-tools": the answer
/// ends with "This model cannot use tools."), Stop, Run of a preview (a Restart task), End and the settings with their
/// refusals (trailer <c>oadm-field</c>).
/// </summary>
public sealed partial class FakeOadmApi
{
    public const string AssistantOff = Contracts.Assistant.AssistantTexts.IsOff;
    public const string AssistantBusy = Contracts.Assistant.AssistantTexts.Busy;
    public const string AssistantPreviewEnded = Contracts.Assistant.AssistantTexts.PreviewEnded;
    public const string AssistantNoTools = Contracts.Assistant.AssistantTexts.CannotUseTools;

    private AssistantSettings _assistant = new()
    {
        Enabled = true, Provider = "anthropic", DailyLimit = 2_000_000,
    };

    private bool _assistantHasKey = true;
    private long _assistantUsed = 18_420;
    private readonly Dictionary<string, CancellationTokenSource> _assistantChats = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _assistantPreviews = new(StringComparer.Ordinal);

    /// <summary>
    /// The answer to a message (tests): the events after the message, <c>done</c> included or not (the fake adds one when the
    /// script ends without it). Null: the default script.
    /// </summary>
    public Func<AssistantMessage, CancellationToken, IAsyncEnumerable<AssistantEvent>>? AssistantScript { get; set; }

    /// <summary>The answer of ListModels (tests); null: a list by provider (none for OpenAI-compatible: editable).</summary>
    public AssistantModelList? AssistantModels { get; set; }

    /// <summary>How many times ListModels was called (tests).</summary>
    public int AssistantModelReads { get; private set; }

    /// <summary>Every message Chat received, in order (tests).</summary>
    public List<AssistantMessage> AssistantMessages { get; } = [];

    /// <summary>The conversations Stop, End and Run were called for (tests).</summary>
    public List<string> AssistantStops { get; } = [];

    public List<string> AssistantEnds { get; } = [];

    public List<AssistantRunRequest> AssistantRuns { get; } = [];

    /// <summary>The settings Test received (tests).</summary>
    public List<AssistantSettings> AssistantTests { get; } = [];

    /// <summary>A preview the default script (or a test) offers; Run starts a Restart task on its devices.</summary>
    public string AddAssistantPreview(IEnumerable<string> deviceIds)
    {
        lock (_gate)
        {
            var handle = Guid.NewGuid().ToString("N");
            _assistantPreviews[handle] = [.. deviceIds];
            return handle;
        }
    }

    public async IAsyncEnumerable<AssistantEvent> AssistantChatAsync(AssistantMessage message, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        CancellationTokenSource stop;
        lock (_gate)
        {
            ThrowIfOffline();
            if (!_assistant.Enabled)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, AssistantOff));
            }

            if (message.Model.Trim().Length == 0)
            {
                throw AssistantRefused("model", Contracts.Assistant.AssistantTexts.ChooseModel);
            }

            if (message.Model.Length > 200)
            {
                throw AssistantRefused("model", Contracts.Assistant.AssistantTexts.ModelTooLong);
            }

            if (_assistantChats.ContainsKey(message.ConversationId))
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, AssistantBusy));
            }

            AssistantMessages.Add(message.Clone());
            stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _assistantChats[message.ConversationId] = stop;
        }

        var done = false;
        try
        {
            var script = message.Model == "no-tools" ? NoTools : AssistantScript ?? DefaultAssistantScript;
            var events = script(message, stop.Token).GetAsyncEnumerator(stop.Token);
            try
            {
                while (true)
                {
                    bool next;
                    try
                    {
                        next = await events.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        break; // Stop
                    }

                    if (!next)
                    {
                        break;
                    }

                    done |= events.Current.KindCase == AssistantEvent.KindOneofCase.Done;
                    yield return events.Current;
                }
            }
            finally
            {
                await events.DisposeAsync().ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            if (!done)
            {
                yield return new AssistantEvent { Done = new AssistantDone { Stopped = stop.IsCancellationRequested, Tokens = 1_240 } };
            }
        }
        finally
        {
            lock (_gate)
            {
                _assistantChats.Remove(message.ConversationId);
                _assistantUsed += 1_240;
            }

            stop.Dispose();
        }
    }

    public Task AssistantStopAsync(string conversationId, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            AssistantStops.Add(conversationId);
            if (_assistantChats.TryGetValue(conversationId, out var chat))
            {
                chat.Cancel();
            }
        }

        return Task.CompletedTask;
    }

    public async Task<AssistantRunReply> AssistantRunAsync(string conversationId, string handle, CancellationToken ct)
    {
        List<string>? devices;
        lock (_gate)
        {
            ThrowIfOffline();
            AssistantRuns.Add(new AssistantRunRequest { ConversationId = conversationId, Handle = handle });
            if (!_assistantPreviews.Remove(handle, out devices))
            {
                throw new RpcException(new Status(StatusCode.NotFound, AssistantPreviewEnded));
            }
        }

        var ids = await RunTaskAsync(RestartPluginId, devices, null, "admin", ct).ConfigureAwait(false);
        lock (_gate)
        {
            _assistantRan.Add(handle);
        }

        var reply = new AssistantRunReply { BatchId = Guid.NewGuid().ToString(), TaskCount = ids.Count };
        reply.TaskIds.AddRange(ids);
        return reply;
    }

    private readonly HashSet<string> _assistantRan = new(StringComparer.Ordinal);

    /// <summary>The secrets a preview's Run needs, by handle (tests: <see cref="AddAssistantPreview(IEnumerable{string}, string[])"/>).</summary>
    private readonly Dictionary<string, string[]> _assistantSecrets = new(StringComparer.Ordinal);

    /// <summary>The files the answers offered, by reference (tests: <see cref="AddAssistantFile"/>).</summary>
    private readonly Dictionary<string, (string Name, byte[] Content)> _assistantFiles = new(StringComparer.Ordinal);

    /// <summary>A preview whose Run needs the secrets <paramref name="secrets"/> (by name); its handle.</summary>
    public string AddAssistantPreview(IEnumerable<string> deviceIds, params string[] secrets)
    {
        var handle = AddAssistantPreview(deviceIds);
        lock (_gate)
        {
            _assistantSecrets[handle] = secrets;
        }

        return handle;
    }

    /// <summary>A file an answer offers (its card's Save fetches it); its reference.</summary>
    public string AddAssistantFile(string name, byte[] content)
    {
        var reference = Guid.NewGuid().ToString("N");
        lock (_gate)
        {
            _assistantFiles[reference] = (name, content);
        }

        return reference;
    }

    public Task<AssistantRunReply> AssistantRunAsync(AssistantRunRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            ThrowIfOffline();
            if (_assistantSecrets.TryGetValue(request.Handle, out var needed)
                && needed.FirstOrDefault(n => !request.Secrets.TryGetValue(n, out var value) || value.Length == 0) is { } missing)
            {
                AssistantRuns.Add(request.Clone());
                throw new RpcException(new Status(StatusCode.FailedPrecondition, Contracts.Assistant.AssistantTexts.EnterSecret("password")),
                    new Metadata { { Contracts.Assistant.AssistantTexts.FieldTrailer, missing } });
            }

            AssistantSecretRuns.Add(request.Clone());
        }

        return AssistantRunAsync(request.ConversationId, request.Handle, ct);
    }

    /// <summary>The runs with their secrets as the server got them (tests: the values went with Run, nowhere else).</summary>
    public List<AssistantRunRequest> AssistantSecretRuns { get; } = [];

    public Task<Oadm.Sdk.Plugins.PluginDownload> AssistantDownloadAsync(string conversationId, string fileRef, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            if (!_assistantFiles.TryGetValue(fileRef, out var file))
            {
                throw new RpcException(new Status(StatusCode.NotFound, Contracts.Assistant.AssistantTexts.FileEnded));
            }

            return Task.FromResult(new Oadm.Sdk.Plugins.PluginDownload(new MemoryStream(file.Content), file.Name, file.Content.Length));
        }
    }

    /// <summary>The Try the failed devices again calls (tests).</summary>
    public List<AssistantRetryRequest> AssistantRetries { get; } = [];

    public Task<AssistantPreviewCard> AssistantRetryFailedAsync(string conversationId, string handle, IReadOnlyList<string> failedDeviceIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(failedDeviceIds);
        lock (_gate)
        {
            ThrowIfOffline();
            var request = new AssistantRetryRequest { ConversationId = conversationId, Handle = handle };
            request.FailedDeviceIds.AddRange(failedDeviceIds);
            AssistantRetries.Add(request);
            if (!_assistantRan.Contains(handle))
            {
                throw new RpcException(new Status(StatusCode.NotFound, AssistantPreviewEnded));
            }
        }

        var card = new AssistantPreviewCard { Handle = AddAssistantPreview(failedDeviceIds), Title = "Restart", DeviceCount = failedDeviceIds.Count };
        card.FirstDevices.AddRange(failedDeviceIds.Take(20));
        return Task.FromResult(card);
    }

    public Task AssistantEndAsync(string conversationId, CancellationToken ct)
    {
        lock (_gate)
        {
            AssistantEnds.Add(conversationId);
            if (_assistantChats.TryGetValue(conversationId, out var chat))
            {
                chat.Cancel();
            }
        }

        return Task.CompletedTask;
    }

    public Task<AssistantSettingsState> GetAssistantSettingsAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            return Task.FromResult(AssistantStateNow());
        }
    }

    public Task<AssistantSettingsState> SetAssistantSettingsAsync(AssistantSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            ThrowIfOffline();
            CheckAssistant(settings);
            var keep = settings.Clone();
            if (keep.ApiKey.Length > 0)
            {
                _assistantHasKey = true;
            }

            keep.ApiKey = string.Empty;
            _assistant = keep;
            return Task.FromResult(AssistantStateNow());
        }
    }

    public async Task<AssistantTestReply> TestAssistantAsync(AssistantSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            ThrowIfOffline();
            AssistantTests.Add(settings.Clone());
            CheckAssistant(settings);
        }

        await Task.Delay(_tick, ct).ConfigureAwait(false);
        return settings.Address.Contains("unreachable", StringComparison.Ordinal)
            ? new AssistantTestReply { Ok = false, Message = "The provider did not answer. Check the address." }
            : new AssistantTestReply { Ok = true, Message = Contracts.Assistant.AssistantTexts.ProviderAnswered };
    }

    public async Task<AssistantModelList> ListAssistantModelsAsync(CancellationToken ct)
    {
        string provider;
        lock (_gate)
        {
            ThrowIfOffline();
            AssistantModelReads++;
            if (!_assistant.Enabled)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, AssistantOff));
            }

            provider = _assistant.Provider;
            if (AssistantModels is { } scripted)
            {
                return scripted.Clone();
            }
        }

        await Task.Delay(_tick, ct).ConfigureAwait(false);
        var list = new AssistantModelList();
        list.Models.AddRange(provider switch
        {
            "anthropic" => ["claude-sonnet-5", "claude-opus-5-5", "claude-haiku-5"],
            "openai" => ["gpt-5.1", "gpt-5.1-mini"],
            "ollama" => ["llama4:16x17b", "qwen3:32b"],
            _ => [],
        });
        list.Editable = list.Models.Count == 0;
        list.Suggested = list.Models.Contains(Contracts.Assistant.AssistantTexts.AnthropicDefaultModel) ? Contracts.Assistant.AssistantTexts.AnthropicDefaultModel : list.Models.FirstOrDefault() ?? string.Empty;
        return list;
    }

    private static async IAsyncEnumerable<AssistantEvent> NoTools(AssistantMessage message, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        yield return new AssistantEvent { Done = new AssistantDone { Error = Contracts.Assistant.AssistantTexts.CannotUseTools } };
    }

    private void CheckAssistant(AssistantSettings settings)
    {
        if (settings.Provider is not ("anthropic" or "openai" or "ollama" or "openai-compatible"))
        {
            throw AssistantRefused("provider", Contracts.Assistant.AssistantTexts.ChooseProvider);
        }

        if (settings.Provider is "ollama" or "openai-compatible"
            && !(Uri.TryCreate(settings.Address, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
        {
            throw AssistantRefused("address", Contracts.Assistant.AssistantTexts.AddressNotValid);
        }

        if (settings.Provider != "ollama" && settings.ApiKey.Length == 0 && !_assistantHasKey)
        {
            throw AssistantRefused("api_key", Contracts.Assistant.AssistantTexts.EnterApiKey);
        }

        if (settings.DailyLimit < 1_000)
        {
            throw AssistantRefused("daily_limit", Contracts.Assistant.AssistantTexts.DailyLimitRange);
        }
    }

    private static RpcException AssistantRefused(string field, string text) =>
        new(new Status(StatusCode.InvalidArgument, text), new Metadata { { "oadm-field", field } });

    private AssistantSettingsState AssistantStateNow()
    {
        var state = new AssistantSettingsState
        {
            Settings = _assistant.Clone(),
            HasApiKey = _assistantHasKey,
            UsedToday = _assistantUsed,
            Status = _assistant.Enabled ? Contracts.Assistant.AssistantTexts.On : Contracts.Assistant.AssistantTexts.Off,
        };
        return state;
    }

    /// <summary>The demo answers of <c>--fake</c>: offline devices, a picture, a restart preview, or a little of every Markdown.</summary>
    private async IAsyncEnumerable<AssistantEvent> DefaultAssistantScript(AssistantMessage message, [EnumeratorCancellation] CancellationToken ct)
    {
        var text = message.Text.ToLowerInvariant();
        List<Device> devices;
        lock (_gate)
        {
            devices = [.. _devices.Select(d => d.Clone())];
        }

        if (AssistantMessages.Count == 1 && _assistant.Provider != "ollama")
        {
            yield return new AssistantEvent { Notice = Contracts.Assistant.AssistantTexts.Notice(_assistant.Provider) };
        }

        if (text.Contains("offline", StringComparison.Ordinal))
        {
            yield return new AssistantEvent { Tool = new AssistantToolLine { Text = "Looking at devices" } };
            await Task.Delay(_tick, ct).ConfigureAwait(false);
            var offline = devices.Where(d => d.Status != DeviceStatus.Ok).ToList();
            yield return new AssistantEvent { Table = TableOf(offline) };
            foreach (var piece in Pieces($"**{offline.Count} devices** are not reachable or need attention. Most of them are at one site."))
            {
                await Task.Delay(_tick, ct).ConfigureAwait(false);
                yield return new AssistantEvent { Text = piece };
            }
        }
        else if (text.Contains("picture", StringComparison.Ordinal))
        {
            var camera = devices.FirstOrDefault(d => d.HasVideo) ?? devices.FirstOrDefault();
            yield return new AssistantEvent { Tool = new AssistantToolLine { Text = "Taking a picture" } };
            await Task.Delay(_tick, ct).ConfigureAwait(false);
            if (camera is not null)
            {
                var jpeg = TestPicture(640, 360, camera.Address, camera.Model, DateTimeOffset.UtcNow, camera.Id.GetHashCode(StringComparison.Ordinal));
                yield return new AssistantEvent
                {
                    Picture = new AssistantPicture { DeviceId = camera.Id, DeviceName = camera.Address, Jpeg = ByteString.CopyFrom(jpeg) },
                };
            }

            yield return new AssistantEvent { Text = "Here is the picture of " + (camera?.Address ?? "the camera") + "." };
        }
        else if (text.Contains("rule", StringComparison.Ordinal) || text.Contains("delete", StringComparison.Ordinal))
        {
            // A rule to save and tags to delete: what Run does in lines, the unit of the count, a red Run for the delete.
            yield return new AssistantEvent { Tool = new AssistantToolLine { Text = "Preparing the rule" } };
            await Task.Delay(_tick, ct).ConfigureAwait(false);
            var rule = new AssistantPreviewCard
            {
                Handle = AddAssistantPreview([]), Title = "Save rule Lobby motion", CountText = "1 rule",
                Details = { "Event: Motion detected", "Sends to: http://10.0.0.5/hook", "Schedule: Every day 18:00 to 06:00" },
            };
            yield return new AssistantEvent { Preview = rule };
            if (text.Contains("delete", StringComparison.Ordinal))
            {
                var delete = new AssistantPreviewCard
                {
                    Handle = AddAssistantPreview([]), Title = "Delete tags", CountText = "2 tags", Destructive = true,
                    Details = { "Deletes: Lobby old, Yard test" }, Warnings = { "The devices keep their other tags." },
                };
                yield return new AssistantEvent { Preview = delete };
            }

            yield return new AssistantEvent { Text = "Press **Run** on a card to apply it." };
        }
        else if (text.Contains("restart", StringComparison.Ordinal))
        {
            var chosen = message.SelectedDeviceIds.Count > 0 ? [.. message.SelectedDeviceIds] : devices.Take(3).Select(d => d.Id).ToList();
            yield return new AssistantEvent { Tool = new AssistantToolLine { Text = $"Checking {chosen.Count} devices for the restart" } };
            await Task.Delay(_tick, ct).ConfigureAwait(false);
            var card = new AssistantPreviewCard
            {
                Handle = AddAssistantPreview(chosen),
                Title = "Restart",
                DeviceCount = chosen.Count,
                ExpiresUtc = DateTime.UtcNow.AddMinutes(10).ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            };
            card.FirstDevices.AddRange(devices.Where(d => chosen.Contains(d.Id)).Take(20).Select(d => d.Address));
            yield return new AssistantEvent { Preview = card };
            yield return new AssistantEvent { Text = "Press **Run** to restart them." };
        }
        else
        {
            const string answer = "OADM manages your Axis devices. I can:\n\n- find devices (**offline**, old AXIS OS, vulnerabilities)\n"
                + "- show a picture of a camera\n- prepare tasks such as `Restart` that you run\n\n"
                + "| Status | Devices |\n|---|---:|\n| Ok | 42 |\n| Unreachable | 3 |\n";
            foreach (var piece in Pieces(answer))
            {
                await Task.Delay(_tick, ct).ConfigureAwait(false);
                yield return new AssistantEvent { Text = piece };
            }
        }
    }

    private static AssistantTable TableOf(List<Device> devices)
    {
        var table = new AssistantTable { Total = devices.Count };
        table.Rows.AddRange(devices.Take(50).Select(d => new AssistantDeviceRow
        {
            DeviceId = d.Id, Name = d.Address, Address = d.Address, Model = d.Model, Status = d.Status.ToString(),
        }));
        table.AllDeviceIds.AddRange(devices.Take(10_000).Select(d => d.Id));
        return table;
    }

    /// <summary>The answer in pieces of a few words, as a provider streams it.</summary>
    private static IEnumerable<string> Pieces(string text)
    {
        for (int i = 0; i < text.Length; i += 24)
        {
            yield return text.Substring(i, Math.Min(24, text.Length - i));
        }
    }
}
