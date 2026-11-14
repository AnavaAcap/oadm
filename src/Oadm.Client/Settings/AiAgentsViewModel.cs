using System.Globalization;
using System.Text;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Oadm.Client.Api;
using Oadm.Client.Dialogs;
using Oadm.Client.Shell;
using Oadm.Contracts.Mcp;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Network;
using Oadm.Sdk.Client.Validation;
using Oadm.Sdk.Network;

namespace Oadm.Client.Settings;

/// <summary>
/// The Settings card AI agents (docs/spec/mcp.md "Transport and certificate"; "MCP" only in the docs): off by default; Turn on,
/// Port and Listen on for administrators (Save), the certificate the PKI policy MCP server picks, and Download CA certificate
/// for the agent's computer. Without PKI: "Turn on PKI to choose the certificate." and no Turn on. Every user sees the state.
/// </summary>
public sealed partial class AiAgentsViewModel : ValidatingViewModel
{
    public const string Intro = "AI agents work in OADM with an access key and the rights of its user.";
    public const string ChooseCertificate = "Choose a certificate in the PKI policy MCP server.";
    public const string PortRange = McpTexts.PortRange;
    public const string NoChanges = "No changes to save.";

    private static readonly FileType PemFile = new("CA certificate", "pem", "application/x-pem-file");

    private readonly IOadmApi _api;
    private readonly IDialogService _dialogs;
    private readonly UserSession? _session;
    private McpState? _state;

    public AiAgentsViewModel(IOadmApi api, IDialogService dialogs, UserSession? session = null)
    {
        _api = api;
        _dialogs = dialogs;
        _session = session;
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

        Listen.PropertyChanged += (_, _) => NotifyCommands();
        Validation.Rule(nameof(Port), () => int.TryParse(Port, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1025 and <= 65535 ? null : PortRange);
        Validation.Validate();
        Validation.Reset();
    }

    /// <summary>Administrators change it; the server checks it too.</summary>
    public bool IsAdmin => _session?.IsAdmin ?? true;

    /// <summary>The choices of Listen on (the server's interfaces).</summary>
    public InterfaceSelection Listen { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(SaveTip))]
    public partial string Port { get; set; } = "5082";

    [ObservableProperty] public partial bool IsLoaded { get; private set; }

    [ObservableProperty] public partial bool IsBusy { get; private set; }

    [ObservableProperty] public partial string? Message { get; private set; }

    [ObservableProperty] public partial bool MessageIsError { get; private set; }

    public bool IsOn => _state?.Settings.Enabled ?? false;

    public bool Running => _state?.Running ?? false;

    /// <summary>"Running on port 5082", "Off", or why it does not listen.</summary>
    public string StatusText => _state?.Status ?? McpTexts.Off;

    /// <summary>On but not listening: the status is a problem.</summary>
    public bool HasProblem => IsOn && !Running;

    /// <summary>The certificate the PKI policy picks, or what to do.</summary>
    public string CertificateText => _state is null ? string.Empty
        : !_state.PkiAvailable ? McpTexts.NoPki
        : _state.Certificate.Length > 0 ? _state.Certificate
        : ChooseCertificate;

    /// <summary>The names the certificate holds (agents connect with one of them).</summary>
    public string? CertificateHint => _state is { CertificateNames.Count: > 0 } state ? "Names: " + string.Join(", ", state.CertificateNames) : null;

    /// <summary>Listen on as plain text (users who read the card).</summary>
    public string ListenText => Listen.Selected?.Label ?? InterfaceOptions.AllLabel;

    public bool ShowTurnOn => IsAdmin && !IsOn;

    public bool ShowTurnOff => IsAdmin && IsOn;

    public bool CanDownloadCa => _state?.HasCaCertificate ?? false;

    /// <summary>Why Save is disabled.</summary>
    public string? SaveTip => Validation.FirstError ?? (Changed ? null : NoChanges);

    /// <summary>Why Turn on is disabled.</summary>
    public string? TurnOnTip => _state is { PkiAvailable: false } ? McpTexts.NoPki : Validation.FirstError;

    private bool Changed => _state is { } state
        && (Port != state.Settings.Port.ToString(CultureInfo.InvariantCulture) || !SameInterface(Listen.SelectedId, state.Settings.ListenOn));

    protected override void OnValidationChanged() => NotifyCommands();

    public async Task LoadAsync()
    {
        try
        {
            Apply(await _api.GetMcpAsync(CancellationToken.None).ConfigureAwait(true));
            IsLoaded = true;
        }
        catch (Exception ex) when (ex is RpcException or NotSupportedException)
        {
            IsLoaded = false;
            MessageIsError = true;
            Message = ex is RpcException rpc ? rpc.Status.Detail : ex.Message;
        }
    }

    private bool CanSave => IsLoaded && IsAdmin && !IsBusy && Validation.IsValid && Changed;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync() => SetAsync(IsOn);

    private bool CanTurnOn => IsLoaded && IsAdmin && !IsBusy && Validation.IsValid && (_state?.PkiAvailable ?? false);

    [RelayCommand(CanExecute = nameof(CanTurnOn))]
    private Task TurnOnAsync() => SetAsync(true);

    private bool CanTurnOff => IsLoaded && IsAdmin && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanTurnOff))]
    private async Task TurnOffAsync()
    {
        if (await _dialogs.ConfirmAsync("Turn off AI agents", "Agents can no longer connect until you turn it on again.", "Turn off").ConfigureAwait(true))
        {
            await SetAsync(false).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task DownloadCaAsync()
    {
        try
        {
            var ca = await _api.GetMcpCaCertificateAsync(CancellationToken.None).ConfigureAwait(true);
            if (await _dialogs.SaveFileAsync("Download CA certificate", ca.FileName, PemFile, Encoding.ASCII.GetBytes(ca.Pem)).ConfigureAwait(true) is { } name)
            {
                MessageIsError = false;
                Message = $"Saved {name}.";
            }
        }
        catch (RpcException ex)
        {
            MessageIsError = true;
            Message = ex.Status.Detail;
        }
    }

    private async Task SetAsync(bool enabled)
    {
        if (!Validation.IsValid)
        {
            Validation.ShowAll();
            return;
        }

        IsBusy = true;
        NotifyCommands();
        try
        {
            var settings = new McpSettings
            {
                Enabled = enabled,
                Port = int.Parse(Port, CultureInfo.InvariantCulture),
                ListenOn = InterfaceOptions.IsAll(Listen.SelectedId) ? string.Empty : Listen.SelectedId!,
            };
            Apply(await _api.SetMcpAsync(settings, CancellationToken.None).ConfigureAwait(true));
            Message = null;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.InvalidArgument && ex.Status.Detail == PortRange)
        {
            Validation.SetServerError(nameof(Port), ex.Status.Detail);
        }
        catch (RpcException ex)
        {
            MessageIsError = true;
            Message = ex.Status.Detail;
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    private void Apply(McpState state)
    {
        _state = state;
        Port = state.Settings.Port.ToString(CultureInfo.InvariantCulture);
        var options = state.Interfaces.Select(i => new InterfaceOption(i.Id, i.Label, i.Name, [.. i.Addresses])).ToList();
        if (options.Count == 0)
        {
            options.Add(InterfaceOptions.All);
        }

        var configured = InterfaceOptions.IsAll(state.Settings.ListenOn) ? InterfaceOptions.AllId : state.Settings.ListenOn;
        Listen.Selected = null;
        Listen.Apply(options, configured, null);
        Validation.Reset();
        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(Running));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasProblem));
        OnPropertyChanged(nameof(CertificateText));
        OnPropertyChanged(nameof(CertificateHint));
        OnPropertyChanged(nameof(ListenText));
        OnPropertyChanged(nameof(CanDownloadCa));
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(ShowTurnOn));
        OnPropertyChanged(nameof(ShowTurnOff));
        OnPropertyChanged(nameof(SaveTip));
        OnPropertyChanged(nameof(TurnOnTip));
        OnPropertyChanged(nameof(ListenText));
        SaveCommand.NotifyCanExecuteChanged();
        TurnOnCommand.NotifyCanExecuteChanged();
        TurnOffCommand.NotifyCanExecuteChanged();
    }

    private static bool SameInterface(string? a, string? b) =>
        InterfaceOptions.IsAll(a) ? InterfaceOptions.IsAll(b) : string.Equals(a, b, StringComparison.Ordinal);
}
