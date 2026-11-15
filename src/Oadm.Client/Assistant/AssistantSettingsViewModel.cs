using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Oadm.Client.Api;
using Oadm.Client.Shell;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Client.Assistant;

/// <summary>
/// The Settings card AI, section Assistant (docs/spec/assistant.md "Settings"), below the section MCP: Turn on / Turn off,
/// Provider, API key (write only, "Saved" once stored; not for Ollama), Address (Ollama, OpenAI-compatible), Send pictures
/// to the model, the daily limits and Used today, Test (reads the provider's model list), Save. No model: each user picks
/// it in the panel. Administrators edit, every other user reads it. Errors below their fields, the server's refusals too (trailer <c>oadm-field</c>).
/// </summary>
public sealed partial class AssistantSettingsViewModel : ValidatingViewModel
{
    public const long DefaultDailyLimit = Contracts.Assistant.AssistantTexts.DefaultDailyLimit;
    public const long MinLimit = Contracts.Assistant.AssistantTexts.MinDailyLimit;
    public const long MaxLimit = Contracts.Assistant.AssistantTexts.MaxDailyLimit;

    private readonly IOadmApi _api;
    private readonly UserSession? _session;
    private readonly AssistantAvailability? _availability;
    private AssistantSettingsState? _state;

    public AssistantSettingsViewModel(IOadmApi api, UserSession? session = null, AssistantAvailability? availability = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
        _session = session;
        _availability = availability;
        if (session is not null)
        {
            session.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(UserSession.IsAdmin))
                {
                    OnPropertyChanged(nameof(IsAdmin));
                    NotifyCommands();
                }
            };
        }

        Validation.Rule(nameof(Provider), () => Provider is null ? AssistantTexts.NoProvider : null);
        Validation.Rule(nameof(Address), () => Provider is not { NeedsAddress: true } ? null
            : Address.Trim().Length == 0 ? AssistantTexts.EnterAddress
            : IsAddress(Address) ? null : AssistantTexts.AddressNotValid);
        Validation.Rule(nameof(ApiKey), () => Provider is not { NeedsKey: true } ? null
            : ApiKey.Length > 500 ? AssistantTexts.ApiKeyTooLong
            : HasApiKey || ApiKey.Length > 0 ? null : AssistantTexts.EnterApiKey);
        Validation.Rule(nameof(DailyLimit), () => ParseLimit(DailyLimit) is >= MinLimit and <= MaxLimit ? null : AssistantTexts.LimitRange);
        Validation.Rule(nameof(UserDailyLimit), () => UserDailyLimit.Trim().Length == 0 || ParseLimit(UserDailyLimit) is 0 or (>= MinLimit and <= MaxLimit)
            ? null : AssistantTexts.UserLimitRange);
        Validation.Rule(nameof(StepsPerAnswer), () => ParseInt(StepsPerAnswer) is >= Contracts.Assistant.AssistantTexts.MinStepsPerAnswer and <= Contracts.Assistant.AssistantTexts.MaxStepsPerAnswer
            ? null : Contracts.Assistant.AssistantTexts.StepsRange);
        Validation.Rule(nameof(AnswerSeconds), () => ParseInt(AnswerSeconds) is >= Contracts.Assistant.AssistantTexts.MinAnswerSeconds and <= Contracts.Assistant.AssistantTexts.MaxAnswerSeconds
            ? null : Contracts.Assistant.AssistantTexts.AnswerSecondsRange);
        Validation.Validate();
        Validation.Reset();
    }

    /// <summary>Administrators change it; the server checks it too.</summary>
    public bool IsAdmin => _session?.IsAdmin ?? true;

    /// <summary>The server has the assistant (an older one answers UNIMPLEMENTED: the section is hidden).</summary>
    [ObservableProperty] public partial bool IsAvailable { get; private set; }

    [ObservableProperty] public partial bool IsLoaded { get; private set; }

    [ObservableProperty] public partial bool IsBusy { get; private set; }

    [ObservableProperty] public partial bool IsTesting { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsKey), nameof(NeedsAddress), nameof(PrivacyNote), nameof(ProviderText))]
    public partial AssistantProvider? Provider { get; set; }

    /// <summary>Write only: empty keeps the saved key.</summary>
    [ObservableProperty] public partial string ApiKey { get; set; } = string.Empty;

    [ObservableProperty] public partial string Address { get; set; } = string.Empty;


    [ObservableProperty] public partial bool SendPictures { get; set; }

    [ObservableProperty] public partial string DailyLimit { get; set; } = DefaultDailyLimit.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Optional; empty: no limit per user.</summary>
    [ObservableProperty] public partial string UserDailyLimit { get; set; } = string.Empty;

    /// <summary>The model's tool calls per message, 1 to 50.</summary>
    [ObservableProperty] public partial string StepsPerAnswer { get; set; } = Contracts.Assistant.AssistantTexts.MaxToolCallsPerMessage.ToString(CultureInfo.InvariantCulture);

    /// <summary>The longest one answer may take, 30 to 600 seconds.</summary>
    [ObservableProperty] public partial string AnswerSeconds { get; set; } = Contracts.Assistant.AssistantTexts.DefaultAnswerSeconds.ToString(CultureInfo.InvariantCulture);

    private static int? ParseInt(string text) => int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;


    /// <summary>The answer of Test, or what is wrong.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestResult))]
    public partial string? TestResult { get; private set; }

    [ObservableProperty] public partial bool TestOk { get; private set; }

    public bool HasTestResult => !string.IsNullOrEmpty(TestResult);

    /// <summary>A refusal that names no field (below the buttons, like the section MCP).</summary>
    [ObservableProperty] public partial string? Message { get; private set; }

    public bool HasApiKey => _state?.HasApiKey ?? false;

    /// <summary>The key box's placeholder: "Saved" once a key is stored.</summary>
    public string? ApiKeyPlaceholder => HasApiKey ? AssistantTexts.Saved : null;

    public bool NeedsKey => Provider?.NeedsKey ?? true;

    public bool NeedsAddress => Provider?.NeedsAddress ?? false;

    /// <summary>Cloud: device data goes to the provider; Ollama keeps it in the network.</summary>
    public string? PrivacyNote => Provider is null ? null : Provider.IsCloud ? AssistantTexts.CloudPrivacy(Provider.Name) : AssistantTexts.OllamaPrivacy;

    public string ProviderText => Provider?.Name ?? string.Empty;

    public bool IsOn => _state?.Settings?.Enabled ?? false;

    public string StatusText => _state?.Status is { Length: > 0 } status ? status : IsOn ? "On" : "Off";

    public string DailyLimitText => AssistantTexts.Tokens(_state?.Settings?.DailyLimit ?? DefaultDailyLimit);

    public string UserDailyLimitText => _state?.Settings?.UserDailyLimit is > 0 and var limit ? AssistantTexts.Tokens(limit) : "None";

    public string StepsPerAnswerText => (_state?.Settings?.StepsPerAnswer is > 0 and var steps ? steps : Contracts.Assistant.AssistantTexts.MaxToolCallsPerMessage).ToString(CultureInfo.InvariantCulture);

    public string AnswerSecondsText => string.Create(CultureInfo.CurrentCulture, $"{(_state?.Settings?.AnswerSeconds is > 0 and var s ? s : Contracts.Assistant.AssistantTexts.DefaultAnswerSeconds)} seconds");

    public string SendPicturesText => _state?.Settings?.SendPictures == true ? "On" : "Off";

    public bool ShowTurnOn => IsAdmin && !IsOn;

    public bool ShowTurnOff => IsAdmin && IsOn;

    public string? SaveTip => Validation.FirstError ?? (Changed ? null : AssistantTexts.NoChanges);

    public string? TurnOnTip => Validation.FirstError;

    public string? TestTip => Validation.FirstErrorOf([nameof(Provider), nameof(Address), nameof(ApiKey)]);

    private bool Changed => _state?.Settings is { } saved && !Same(saved, Build(saved.Enabled));

    protected override void OnValidationChanged() => NotifyCommands();

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Provider) or nameof(ApiKey) or nameof(Address) or nameof(SendPictures)
            or nameof(DailyLimit) or nameof(UserDailyLimit) or nameof(StepsPerAnswer) or nameof(AnswerSeconds))
        {
            OnPropertyChanged(nameof(SaveTip));
            SaveCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnProviderChanged(AssistantProvider? value) => TestResult = null;

    public async Task LoadAsync()
    {
        try
        {
            if (await _api.GetAssistantSettingsAsync(CancellationToken.None).ConfigureAwait(true) is not { } state)
            {
                IsAvailable = false; // no answer to show (a test double without the service)
                return;
            }

            Apply(state);
            IsAvailable = true;
            IsLoaded = true;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
        {
            IsAvailable = false; // an older server: no section
            IsLoaded = false;
        }
        catch (RpcException ex)
        {
            IsAvailable = true;
            IsLoaded = false;
            Message = ex.Status.Detail;
        }
    }

    private bool CanSave => IsLoaded && IsAdmin && !IsBusy && Validation.IsValid && Changed;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync() => SetAsync(IsOn);

    /// <summary>Turn on saves the form too: off while any field has an error (its tooltip says which).</summary>
    private bool CanTurnOn => IsLoaded && IsAdmin && !IsBusy && Validation.IsValid;

    [RelayCommand(CanExecute = nameof(CanTurnOn))]
    private Task TurnOnAsync() => SetAsync(true);

    private bool CanTurnOff => IsLoaded && IsAdmin && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanTurnOff))]
    private Task TurnOffAsync() => SetAsync(false, savedValues: true);

    private bool CanTest => IsLoaded && IsAdmin && !IsBusy && !IsTesting
        && Validation.IsValidFor(nameof(Provider), nameof(Address), nameof(ApiKey));

    /// <summary>One short call with the settings of the form (the saved key when the box is empty); never saves.</summary>
    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestAsync()
    {
        IsTesting = true;
        TestResult = null;
        NotifyCommands();
        try
        {
            var reply = await _api.TestAssistantAsync(Build(IsOn), CancellationToken.None).ConfigureAwait(true);
            TestOk = reply.Ok;
            TestResult = reply.Message;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.InvalidArgument && FieldOf(ex) is { } field)
        {
            Validation.SetServerError(field, ex.Status.Detail);
        }
        catch (RpcException ex)
        {
            TestOk = false;
            TestResult = ex.Status.Detail;
        }
        finally
        {
            IsTesting = false;
            NotifyCommands();
        }
    }

    /// <param name="savedValues">Turn off: the saved settings with only Enabled changed, never the edited fields.</param>
    private async Task SetAsync(bool enabled, bool savedValues = false)
    {
        if (!savedValues && !Validation.IsValid)
        {
            Validation.ShowAll();
            return;
        }

        IsBusy = true;
        NotifyCommands();
        try
        {
            var settings = savedValues && _state?.Settings is { } saved ? saved.Clone() : Build(enabled);
            settings.Enabled = enabled;
            settings.ApiKey = savedValues ? string.Empty : settings.ApiKey; // empty keeps the saved key
            Apply(await _api.SetAssistantSettingsAsync(settings, CancellationToken.None).ConfigureAwait(true));
            Message = null;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.InvalidArgument && FieldOf(ex) is { } field)
        {
            Validation.SetServerError(field, ex.Status.Detail);
        }
        catch (RpcException ex)
        {
            Message = ex.Status.Detail;
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    /// <summary>The field a refusal names (trailer <c>oadm-field</c>); null without one.</summary>
    private static string? FieldOf(RpcException ex) => ex.Trailers.GetValue("oadm-field") switch
    {
        "provider" => nameof(Provider),
        "address" => nameof(Address),
        "api_key" => nameof(ApiKey),
        "daily_limit" => nameof(DailyLimit),
        "user_daily_limit" => nameof(UserDailyLimit),
        "steps_per_answer" => nameof(StepsPerAnswer),
        "answer_seconds" => nameof(AnswerSeconds),
        _ => null,
    };

    private AssistantSettings Build(bool enabled) => new()
    {
        Enabled = enabled,
        Provider = Provider?.Id ?? string.Empty,
        Address = NeedsAddress ? Address.Trim() : string.Empty,
        ApiKey = NeedsKey ? ApiKey : string.Empty,
        SendPictures = SendPictures,
        DailyLimit = ParseLimit(DailyLimit) ?? 0,
        UserDailyLimit = UserDailyLimit.Trim().Length == 0 ? 0 : ParseLimit(UserDailyLimit) ?? 0,
        StepsPerAnswer = ParseInt(StepsPerAnswer) ?? 0,
        AnswerSeconds = ParseInt(AnswerSeconds) ?? 0,
    };

    private static bool Same(AssistantSettings saved, AssistantSettings form) =>
        saved.Provider == form.Provider && saved.Address == form.Address && form.ApiKey.Length == 0
        && saved.SendPictures == form.SendPictures && saved.DailyLimit == form.DailyLimit && saved.UserDailyLimit == form.UserDailyLimit
        && OrDefault(saved.StepsPerAnswer, Contracts.Assistant.AssistantTexts.MaxToolCallsPerMessage) == form.StepsPerAnswer
        && OrDefault(saved.AnswerSeconds, Contracts.Assistant.AssistantTexts.DefaultAnswerSeconds) == form.AnswerSeconds;

    /// <summary>0 from an older server: the default.</summary>
    private static int OrDefault(int value, int fallback) => value > 0 ? value : fallback;

    private void Apply(AssistantSettingsState state)
    {
        _state = state;
        var settings = state.Settings ?? new AssistantSettings();
        Provider = AssistantProvider.Find(settings.Provider) ?? AssistantProvider.Anthropic;
        Address = settings.Address;
        ApiKey = string.Empty;
        SendPictures = settings.SendPictures;
        DailyLimit = (settings.DailyLimit > 0 ? settings.DailyLimit : DefaultDailyLimit).ToString("N0", CultureInfo.InvariantCulture);
        UserDailyLimit = settings.UserDailyLimit > 0 ? settings.UserDailyLimit.ToString("N0", CultureInfo.InvariantCulture) : string.Empty;
        StepsPerAnswer = (settings.StepsPerAnswer > 0 ? settings.StepsPerAnswer : Contracts.Assistant.AssistantTexts.MaxToolCallsPerMessage).ToString(CultureInfo.InvariantCulture);
        AnswerSeconds = (settings.AnswerSeconds > 0 ? settings.AnswerSeconds : Contracts.Assistant.AssistantTexts.DefaultAnswerSeconds).ToString(CultureInfo.InvariantCulture);
        Validation.Reset();
        _availability?.Apply(state);
        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasApiKey));
        OnPropertyChanged(nameof(ApiKeyPlaceholder));
        OnPropertyChanged(nameof(DailyLimitText));
        OnPropertyChanged(nameof(UserDailyLimitText));
        OnPropertyChanged(nameof(StepsPerAnswerText));
        OnPropertyChanged(nameof(AnswerSecondsText));
        OnPropertyChanged(nameof(SendPicturesText));
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(ShowTurnOn));
        OnPropertyChanged(nameof(ShowTurnOff));
        OnPropertyChanged(nameof(SaveTip));
        OnPropertyChanged(nameof(TurnOnTip));
        OnPropertyChanged(nameof(TestTip));
        SaveCommand.NotifyCanExecuteChanged();
        TurnOnCommand.NotifyCanExecuteChanged();
        TurnOffCommand.NotifyCanExecuteChanged();
        TestCommand.NotifyCanExecuteChanged();
    }

    private static long? ParseLimit(string text) =>
        long.TryParse(text.Trim().Replace(",", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal),
            NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static bool IsAddress(string text) =>
        Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0;
}
