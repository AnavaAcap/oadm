using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.VpnFleet.Client;

public enum WizardMode
{
    Install,
    Setup,
}

/// <summary>
/// The wizard of an action on many cameras ("Install or upgrade", "Set up many cameras"): 1 the app (and for a set up its
/// settings), 2 the cameras in a table with a tick each, 3 what will happen, then Start. One task per ticked camera.
/// </summary>
public sealed partial class RolloutWizardViewModel : ObservableObject
{
    public const int AppStep = 0;
    public const int CamerasStep = 1;
    public const int CheckStep = 2;

    private readonly Func<VpnKind, Guid?, string?, IReadOnlyList<CameraCheck>> _build;
    private readonly Func<RolloutWizardViewModel, Task<(string? Info, string? Error)>> _start;
    private (VpnKind Kind, Guid? Hub, string? Version)? _built;

    public RolloutWizardViewModel(
        WizardMode mode,
        InstallViewModel? install,
        ConnectFormViewModel? form,
        Func<VpnKind, Guid?, string?, IReadOnlyList<CameraCheck>> build,
        Func<RolloutWizardViewModel, Task<(string? Info, string? Error)>> start)
    {
        Mode = mode;
        Install = install;
        Form = form;
        _build = build;
        _start = start;
        Steps =
        [
            new WizardStepItem(AppStep, mode == WizardMode.Install ? "App" : "VPN", () => Step, CanOpen, Open),
            new WizardStepItem(CamerasStep, "Cameras", () => Step, CanOpen, Open),
            new WizardStepItem(CheckStep, "Check and start", () => Step, CanOpen, Open),
        ];
        Cameras.Changed += (_, _) => Notify();
        if (install is not null)
        {
            install.PropertyChanged += (_, _) => Notify();
        }

        if (form is not null)
        {
            form.PropertyChanged += (_, _) => Notify();
        }
    }

    public WizardMode Mode { get; }

    public bool IsInstall => Mode == WizardMode.Install;

    public bool IsSetup => Mode == WizardMode.Setup;

    public string Title => IsInstall ? "Install or upgrade" : "Set up many cameras";

    public InstallViewModel? Install { get; }

    public ConnectFormViewModel? Form { get; }

    public IReadOnlyList<WizardStepItem> Steps { get; }

    public CameraChecklist Cameras { get; } = new();

    /// <summary>The devices the hub can be chosen from; set by the page.</summary>
    public Func<IReadOnlyList<IDeviceInfo>>? HubCandidates { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAppStep), nameof(IsCamerasStep), nameof(IsCheckStep), nameof(CanBack), nameof(IsInstallAppStep), nameof(IsSetupAppStep))]
    public partial int Step { get; private set; }

    public bool IsAppStep => Step == AppStep;

    public bool IsInstallAppStep => IsAppStep && IsInstall;

    public bool IsSetupAppStep => IsAppStep && IsSetup;

    public bool IsCamerasStep => Step == CamerasStep;

    public bool IsCheckStep => Step == CheckStep;

    public bool CanBack => Step > AppStep && !IsStarting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBack))]
    public partial bool IsStarting { get; private set; }

    [ObservableProperty]
    public partial string? ErrorText { get; private set; }

    /// <summary>The lines of the last step: what happens to how many cameras.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> Plan { get; private set; } = [];

    /// <summary>A warning of the last step (ticked cameras the task will refuse), or null.</summary>
    [ObservableProperty]
    public partial string? PlanWarning { get; private set; }

    /// <summary>What the page shows after the wizard started the tasks; null when it was cancelled.</summary>
    public string? Result { get; private set; }

    public event EventHandler? CloseRequested;

    private VpnKind Kind => IsInstall ? Install!.SelectedApp.Kind : Form!.Kind;

    private string AppName => VpnApps.NameOf(Kind);

    public string NextText => Step switch
    {
        CheckStep when IsInstall => Cameras.CheckedCount == 1 ? "Install or upgrade 1 camera" : $"Install or upgrade {Cameras.CheckedCount:N0} cameras",
        CheckStep => Cameras.CheckedCount == 1 ? "Set up 1 camera" : $"Set up {Cameras.CheckedCount:N0} cameras",
        _ => "Next",
    };

    /// <summary>Why Next is off, for its tooltip; null when it is on.</summary>
    public string? NextBlockedReason => IsStarting ? "Starting the tasks."
        : Step switch
        {
            AppStep when IsInstall => Install!.LatestVersion is null ? (Install.IsChecking ? "Checking GitHub." : "The newest version is not known.") : null,
            AppStep => Form!.FormError,
            _ => Cameras.CheckedCount == 0 ? "Tick the cameras first." : null,
        };

    public bool CanNext => NextBlockedReason is null;

    [RelayCommand]
    private async Task NextAsync()
    {
        if (Step == AppStep && IsSetup)
        {
            Form!.ShowAllErrors();
        }

        if (!CanNext)
        {
            return;
        }

        switch (Step)
        {
            case AppStep:
                BuildCameras();
                Open(CamerasStep);
                break;
            case CamerasStep:
                MakePlan();
                Open(CheckStep);
                break;
            default:
                await StartAsync();
                break;
        }
    }

    [RelayCommand]
    private void Back()
    {
        if (CanBack)
        {
            Open(Step - 1);
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Install: tick exactly the cameras with an older version.</summary>
    [RelayCommand]
    private void TickOutdated() => Cameras.CheckOnly(CheckKind.Upgrade);

    /// <summary>Install: tick exactly the cameras without the app.</summary>
    [RelayCommand]
    private void TickWithoutApp() => Cameras.CheckOnly(CheckKind.Install);

    /// <summary>Set up: tick exactly the cameras with the app.</summary>
    [RelayCommand]
    private void TickWithApp() => Cameras.CheckOnly(CheckKind.Ready);

    /// <summary>
    /// The cameras for the chosen app; built again only when the app, the hub or the version changed, so the ticks stay
    /// when the user goes back and forth.
    /// </summary>
    private void BuildCameras()
    {
        var hub = IsSetup && Form!.IsWireGuardHub ? Form.HubDeviceId : null;
        var key = (Kind, hub, IsInstall ? Install!.LatestVersion : null);
        if (_built != key)
        {
            Cameras.Search = "";
            Cameras.Set(_build(key.Kind, key.hub, key.Item3));
            _built = key;
        }
    }

    /// <summary>What happens to the ticked cameras, one line per kind.</summary>
    public void MakePlan()
    {
        var ticked = Cameras.CheckedByKind();
        int Count(CheckKind kind) => ticked.GetValueOrDefault(kind);
        static string CountText(int n) => n == 1 ? "1 camera" : $"{n:N0} cameras";
        var lines = new List<string>();
        string? warning = null;
        if (IsInstall)
        {
            var version = Install!.LatestVersion;
            if (Count(CheckKind.Upgrade) > 0)
            {
                lines.Add($"Upgrade {AppName} to {version} on {CountText(Count(CheckKind.Upgrade))}. Their settings stay.");
            }

            if (Count(CheckKind.Install) > 0)
            {
                lines.Add($"Install {AppName} {version} on {CountText(Count(CheckKind.Install))}.");
            }

            if (Count(CheckKind.UpToDate) > 0)
            {
                lines.Add($"Not changed: {CountText(Count(CheckKind.UpToDate))} with {version} already.");
            }

            if (Count(CheckKind.NotRead) + Count(CheckKind.Failed) > 0)
            {
                lines.Add($"Checked first: {CountText(Count(CheckKind.NotRead) + Count(CheckKind.Failed))} not read yet. Each gets the package that fits it.");
            }
        }
        else
        {
            if (Count(CheckKind.Ready) > 0)
            {
                lines.Add($"Set up {AppName} on {CountText(Count(CheckKind.Ready))}: save the settings, start the app and wait for the tunnel.");
            }

            if (Count(CheckKind.NotRead) > 0)
            {
                lines.Add($"Set up {AppName} on {CountText(Count(CheckKind.NotRead))} not read yet. A camera without the app is not changed.");
            }

            if (Count(CheckKind.NoApp) + Count(CheckKind.Failed) > 0)
            {
                warning = $"Ticked without the app or not readable: {CountText(Count(CheckKind.NoApp) + Count(CheckKind.Failed))}. These tasks fail and change nothing.";
            }

            if (Form!.IsWireGuardHub && Form.HubName is { } hub)
            {
                lines.Add($"The hub {hub} gets the cameras as peers.");
            }
        }

        Plan = lines;
        PlanWarning = warning;
    }

    private async Task StartAsync()
    {
        IsStarting = true;
        ErrorText = null;
        Notify();
        try
        {
            var (info, error) = await _start(this);
            if (error is not null)
            {
                ErrorText = error;
                return;
            }

            Result = info;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsStarting = false;
            Notify();
        }
    }

    private bool CanOpen(int index) => !IsStarting && index switch
    {
        AppStep => true,
        CamerasStep => _built is not null && (IsInstall ? Install!.LatestVersion is not null : Form!.IsFormValid),
        _ => _built is not null && Cameras.CheckedCount > 0,
    };

    private void Open(int index)
    {
        if (index == CamerasStep && Step == AppStep)
        {
            BuildCameras();
        }

        if (index == CheckStep)
        {
            MakePlan();
        }

        Step = index;
        Notify();
    }

    private void Notify()
    {
        foreach (var step in Steps)
        {
            step.Refresh();
        }

        OnPropertyChanged(nameof(NextText));
        OnPropertyChanged(nameof(NextBlockedReason));
        OnPropertyChanged(nameof(CanNext));
    }
}
