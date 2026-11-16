using System.Text.Json;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Plugins.VpnFleet.Client;

/// <summary>
/// One setting of the camera panel: its value, whether the user changed it, and its problem (below the field once it was
/// edited or left, like every form).
/// </summary>
public sealed partial class SettingFieldViewModel : ValidatingViewModel
{
    private readonly VpnApp _app;

    public SettingFieldViewModel(VpnApp app, SettingValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _app = app;
        Source = value;
        Original = value.Value;
        Value = value.Value;
        SelectedOption = value.Options.FirstOrDefault(o => string.Equals(o.Value, value.Value, StringComparison.OrdinalIgnoreCase));
        Validation.Rule(nameof(Value), () => IsDirty ? AppSettings.Validate(_app, new Dictionary<string, string> { [Name] = Value }, [Name]) is { Count: > 0 } problems ? problems[0].Message : null : null);
        Validation.Validate();
    }

    public SettingValue Source { get; }

    public string Name => Source.Name;

    public string Label => Source.Label;

    public string? Hint => Source.Hint;

    public IReadOnlyList<SettingOption> Options => Source.Options;

    public bool IsText => Source.Kind == SettingKind.Text;

    public bool IsSecret => Source.Kind == SettingKind.Secret;

    public bool IsBool => Source.Kind == SettingKind.Bool;

    public bool IsChoice => Source.Kind == SettingKind.Choice;

    /// <summary>A secret shows whether it is set, never its value.</summary>
    public string? Placeholder => IsSecret ? (Source.HasValue ? "Set. Enter a new one to replace it." : "Not set") : Source.Placeholder;

    public string Original { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(BoolValue))]
    public partial string Value { get; set; }

    [ObservableProperty]
    public partial SettingOption? SelectedOption { get; set; }

    /// <summary>The error shown below the field (after an edit or once the user left it).</summary>
    public string? Error => ErrorOf(nameof(Value));

    /// <summary>The error of the value, shown or not: Save stays off while there is one.</summary>
    public string? Problem => FormError;

    public bool BoolValue
    {
        get => Options.Count > 0 && string.Equals(Value, Options[0].Value, StringComparison.OrdinalIgnoreCase);
        set => Value = Options.Count < 2 ? Value : value ? Options[0].Value : Options[1].Value;
    }

    /// <summary>Changed by the user; an empty secret is no change.</summary>
    public bool IsDirty => IsSecret ? Value.Length > 0 : !string.Equals(Value.Trim(), Original.Trim(), StringComparison.Ordinal);

    partial void OnSelectedOptionChanged(SettingOption? value)
    {
        if (value is not null)
        {
            Value = value.Value;
        }
    }

    protected override void OnValidationChanged()
    {
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(Problem));
    }

    /// <summary>After a save: the saved value is the new original.</summary>
    public void Accept()
    {
        Original = IsSecret ? "" : Value;
        if (IsSecret)
        {
            Value = "";
        }

        Validation.Reset(nameof(Value));
        OnPropertyChanged(nameof(IsDirty));
    }
}

/// <summary>
/// The panel of one selected row: the app's version and upgrade, its settings with Save for this camera only, and for
/// a camera without a VPN app the install of one.
/// </summary>
public sealed partial class CameraSettingsViewModel : ObservableObject
{
    private readonly ICorePluginClientContext _ctx;
    private readonly CancellationToken _stop;

    public CameraSettingsViewModel(ICorePluginClientContext ctx, FleetRowView row, bool mayChange, bool mayInstall, CancellationToken stop)
    {
        _ctx = ctx;
        _stop = stop;
        Row = row;
        MayChange = mayChange;
        MayInstall = mayInstall;
        App = row.Row.Vpn is { } kind ? VpnApps.Of(kind) : null;
        SelectedInstallApp = InstallApps[0];
    }

    public FleetRowView Row { get; }

    public VpnApp? App { get; }

    public bool HasApp => App is not null && Row.Row.Error is null;

    public bool HasNoApp => Row.Row.Error is null && Row.Row.State == VpnState.NoVpnApp;

    public bool MayChange { get; }

    public bool MayInstall { get; }

    public string Title => App is null ? Row.Address : $"{App.DisplayName} on {Row.Address}";

    public string VersionText => Row.Row.AppVersion is null ? ""
        : Row.Row.UpdateAvailable ? $"Version {Row.Row.AppVersion}. Version {Row.Row.LatestVersion} is available."
        : Row.Row.LatestVersion is null ? $"Version {Row.Row.AppVersion}."
        : $"Version {Row.Row.AppVersion}, the newest.";

    public bool CanUpgrade => MayInstall && Row.Row.UpdateAvailable;

    public string UpgradeText => $"Upgrade to {Row.Row.LatestVersion}";

    public IReadOnlyList<VpnChoice> InstallApps { get; } = [.. VpnApps.All.Select(a => new VpnChoice(a.Kind, a.DisplayName))];

    [ObservableProperty]
    public partial VpnChoice SelectedInstallApp { get; set; }

    public System.Collections.ObjectModel.ObservableCollection<SettingFieldViewModel> Fields { get; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(NewKeyPairCommand))]
    public partial bool IsSaving { get; private set; }

    [ObservableProperty]
    public partial string? ErrorText { get; private set; }

    [ObservableProperty]
    public partial string? InfoText { get; private set; }

    /// <summary>WireGuard: the camera's public key, for its server.</summary>
    [ObservableProperty]
    public partial string? PublicKey { get; private set; }

    public bool IsWireGuard => App?.Kind is VpnKind.WireGuard or VpnKind.WireGuardServer;

    /// <summary>A WireGuard camera (never the hub: its key is every camera's peer key) can get a new key pair.</summary>
    public bool ShowNewKeyPair => App?.Kind == VpnKind.WireGuard;

    public bool IsOpenVpn => App?.Kind == VpnKind.OpenVpn;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string? Profile { get; private set; }

    [ObservableProperty]
    public partial string? ProfileName { get; private set; }

    /// <summary>Why the chosen file cannot be the profile; shown below the profile field.</summary>
    [ObservableProperty]
    public partial string? ProfileError { get; private set; }

    /// <summary>Opens a file dialog for the OpenVPN profile; set by the view.</summary>
    public Func<Task<ProfileFile?>>? PickProfile { get; set; }

    /// <summary>Opens the release page; set by the page, which opens nothing but GitHub.</summary>
    public Action<string>? OpenUrl { get; set; }

    /// <summary>Starts an install or upgrade (the page owns it): app, devices, upgrade.</summary>
    public Func<VpnKind, IReadOnlyList<Guid>, bool, Task<string?>>? StartInstall { get; set; }

    public async Task LoadAsync()
    {
        if (!HasApp)
        {
            return;
        }

        IsLoading = true;
        ErrorText = null;
        try
        {
            string? reply = null;
            ErrorText = await GuardedRun.RunAsync(
                async () => reply = await _ctx.InvokeAsync(Fleet.GetSettings, JsonSerializer.Serialize(new SettingsRequest(Row.Row.DeviceId, App!.Kind), Fleet.Json), _stop),
                GuardedCancel.Ignore);
            if (reply is not null && JsonSerializer.Deserialize<SettingsReply>(reply, Fleet.Json) is { } settings)
            {
                foreach (var old in Fields)
                {
                    old.PropertyChanged -= OnFieldChanged;
                }

                Fields.Clear();
                foreach (var value in settings.Fields)
                {
                    var field = new SettingFieldViewModel(App!, value);
                    field.PropertyChanged += OnFieldChanged;
                    Fields.Add(field);
                }

                PublicKey = settings.PublicKey;
            }
        }
        finally
        {
            IsLoading = false;
            SaveCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnFieldChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingFieldViewModel.IsDirty) or nameof(SettingFieldViewModel.Problem))
        {
            SaveCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(SaveBlockedReason));
        }
    }

    public string? SaveBlockedReason =>
        !MayChange ? "Your role cannot change VPN settings."
        : Fields.FirstOrDefault(f => f.Problem is not null)?.Problem
        ?? ProfileError
        ?? (Fields.Any(f => f.IsDirty) || Profile is not null ? null : "Change a setting first.");

    private bool CanSave() => !IsSaving && HasApp && SaveBlockedReason is null;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        IsSaving = true;
        ErrorText = null;
        InfoText = null;
        try
        {
            var changed = Fields.Where(f => f.IsDirty).ToList();
            var request = new SaveSettingsRequest(Row.Row.DeviceId, App!.Kind, changed.ToDictionary(f => f.Name, f => f.Value)) { Profile = Profile };
            string? reply = null;
            ErrorText = await GuardedRun.RunAsync(
                async () => reply = await _ctx.InvokeAsync(Fleet.SaveSettings, JsonSerializer.Serialize(request, Fleet.Json), _stop),
                GuardedCancel.Ignore);
            if (reply is not null && JsonSerializer.Deserialize<SaveSettingsReply>(reply, Fleet.Json) is { } saved)
            {
                foreach (var field in changed)
                {
                    field.Accept();
                }

                Profile = null;
                ProfileName = null;
                InfoText = (saved.Changed == 1 ? "Saved 1 setting. The app restarts with it." : $"Saved {saved.Changed} settings. The app restarts with them.")
                    + (saved.Note is null ? "" : " " + saved.Note);
                if (changed.Any(f => f.Name == "PrivateKey"))
                {
                    await LoadAsync();
                }
            }
        }
        finally
        {
            IsSaving = false;
            OnPropertyChanged(nameof(SaveBlockedReason));
        }
    }

    private bool CanNewKeyPair() => MayChange && ShowNewKeyPair && !IsSaving;

    /// <summary>
    /// WireGuard: the server makes a new key pair and writes the private key to this camera only (it never comes here);
    /// the new public key shows for the camera's server. A hub camera gets it by itself.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanNewKeyPair))]
    private async Task NewKeyPairAsync()
    {
        if (!await _ctx.ConfirmAsync("New key pair", $"Make a new key pair for {Row.Address}? The camera uses it right away.", "New key pair"))
        {
            return;
        }

        IsSaving = true;
        ErrorText = null;
        InfoText = null;
        try
        {
            string? reply = null;
            ErrorText = await GuardedRun.RunAsync(
                async () => reply = await _ctx.InvokeAsync(Fleet.NewKeyPair, JsonSerializer.Serialize(new NewKeyPairRequest(Row.Row.DeviceId), Fleet.Json), _stop),
                GuardedCancel.Ignore);
            if (reply is not null && JsonSerializer.Deserialize<NewKeyPairReply>(reply, Fleet.Json) is { } made)
            {
                PublicKey = made.PublicKey;
                InfoText = "The camera has a new key pair." + (made.Note is null ? "" : " " + made.Note);
            }
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    private async Task ChooseProfileAsync()
    {
        if (PickProfile is { } pick && await pick() is { } file)
        {
            ProfileName = file.Name;
            ProfileError = file.Problem;
            Profile = ProfileError is null ? file.Text : null;
            OnPropertyChanged(nameof(SaveBlockedReason));
            SaveCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private async Task UpgradeAsync()
    {
        if (StartInstall is { } start && App is not null)
        {
            ErrorText = null;
            InfoText = await start(App.Kind, [Row.Row.DeviceId], true);
        }
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (StartInstall is { } start)
        {
            ErrorText = null;
            InfoText = await start(SelectedInstallApp.Kind, [Row.Row.DeviceId], false);
        }
    }

    [RelayCommand]
    private void OpenRelease()
    {
        if (App is not null)
        {
            OpenUrl?.Invoke($"https://github.com/{VpnApps.Owner}/{App.Repository}/releases/latest");
        }
    }
}
