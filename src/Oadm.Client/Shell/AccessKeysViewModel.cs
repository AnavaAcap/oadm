using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Oadm.Client.Api;
using Oadm.Client.Dialogs;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Client.Shell;

/// <summary>One lifetime of a new access key: "90 days".</summary>
public sealed record AccessKeyLifetime(int Days)
{
    public override string ToString() => string.Create(CultureInfo.CurrentCulture, $"{Days} days");
}

/// <summary>One access key of a list (the user's own, or every user's on the Permissions page).</summary>
public sealed class AccessKeyRow(AccessKey key)
{
    public AccessKey Key { get; } = key ?? throw new ArgumentNullException(nameof(key));

    public string Id => Key.Id;

    public string UserName => Key.UserName;

    public string Name => Key.Name;

    public string Created => AccessKeyWords.Date(Key.Created);

    public string Expires => AccessKeyWords.Date(Key.Expires);

    public string LastUsed => Key.LastUsed is null ? AccessKeyWords.NeverUsed : AccessKeyWords.Time(Key.LastUsed);

    public string ReadOnlyText => Key.ReadOnly ? "Yes" : "No";

    /// <summary>The sort key of the dates (newest last).</summary>
    public string CreatedSortKey => Key.Created?.ToDateTime().ToString("o", CultureInfo.InvariantCulture) ?? string.Empty;
}

/// <summary>
/// A list of access keys the shared grid shows (<see cref="Controls.AccessKeyGrid"/>): the user's own (account menu) or every
/// user's with the User column (Permissions page).
/// </summary>
public interface IAccessKeyList
{
    RangeObservableCollection<AccessKeyRow> Keys { get; }

    bool HasKeys { get; }

    /// <summary>The keys of every user: the grid shows the User column.</summary>
    bool ShowsUser { get; }

    /// <summary>Delete of one row (the parameter is its <see cref="AccessKeyRow"/>).</summary>
    System.Windows.Input.ICommand DeleteKeyCommand { get; }

    /// <summary>What the empty list says.</summary>
    string EmptyText { get; }
}

/// <summary>The texts of access keys in the client (one phrase per concept).</summary>
public static class AccessKeyWords
{
    public const string Title = "Access keys";
    public const string Intro = "An AI agent works in OADM with an access key and the rights of your role.";
    public const string CopyNow = "Copy it now. OADM cannot show it again.";
    public const string NeverUsed = "Never";
    public const string EnterName = "Enter a name.";
    public const string NameTooLong = "Use at most 64 characters.";
    public const string Empty = "No access keys yet. Create one for each AI agent.";
    public const string EmptyAll = "No user has an access key.";

    public static string Date(Google.Protobuf.WellKnownTypes.Timestamp? time) =>
        time is null ? string.Empty : time.ToDateTime().ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);

    public static string Time(Google.Protobuf.WellKnownTypes.Timestamp? time) =>
        time is null ? string.Empty : time.ToDateTime().ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
}

/// <summary>
/// The window Access keys of the account menu (docs/spec/mcp.md "Access keys"): the user's own keys with Delete, and a new key
/// with a name (1 to 64 characters, unique per user), 30, 90 or 365 days and Read only. OADM shows the new key once, here,
/// with Copy; the server keeps only its SHA-256. Errors below their fields; the server's refusals below the field they name.
/// </summary>
public sealed partial class AccessKeysViewModel : ValidatingViewModel, IAccessKeyList
{
    private readonly IOadmApi _api;
    private readonly IDialogService _dialogs;
    private readonly IClipboardService? _clipboard;

    public AccessKeysViewModel(IOadmApi api, IDialogService dialogs, IClipboardService? clipboard = null)
    {
        _api = api;
        _dialogs = dialogs;
        _clipboard = clipboard;
        Validation.Rule(nameof(Name), () => Name.Trim().Length == 0 ? AccessKeyWords.EnterName : Name.Trim().Length > 64 ? AccessKeyWords.NameTooLong : null);
        Validation.Validate();
        Validation.Reset();
    }

    public static IReadOnlyList<AccessKeyLifetime> Lifetimes { get; } = [new(30), new(90), new(365)];

    /// <summary>The user's keys, the newest first.</summary>
    public RangeObservableCollection<AccessKeyRow> Keys { get; } = [];

    public bool HasKeys => Keys.Count > 0;

    public bool ShowsUser => false;

    public System.Windows.Input.ICommand DeleteKeyCommand => DeleteCommand;

    public string EmptyText => AccessKeyWords.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty] public partial AccessKeyLifetime Lifetime { get; set; } = Lifetimes[1];

    [ObservableProperty] public partial bool ReadOnly { get; set; }

    /// <summary>The new key, shown once until the window closes or the next key is made.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNewKey))]
    public partial string? NewKey { get; private set; }

    [ObservableProperty] public partial string? NewKeyTitle { get; private set; }

    public bool HasNewKey => NewKey is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty] public partial string? Message { get; private set; }

    [ObservableProperty] public partial bool MessageIsError { get; private set; }

    /// <summary>Why Create is disabled (tooltip).</summary>
    public string? CreateTip => Validation.FirstError;

    protected override void OnValidationChanged()
    {
        OnPropertyChanged(nameof(CreateTip));
        CreateCommand.NotifyCanExecuteChanged();
    }

    partial void OnNameChanged(string value) => Validation.SetServerError(nameof(Name), null);

    public async Task LoadAsync()
    {
        try
        {
            Show(await _api.ListAccessKeysAsync(everyUser: false, CancellationToken.None).ConfigureAwait(true));
        }
        catch (Exception ex) when (ex is RpcException or NotSupportedException)
        {
            MessageIsError = true;
            Message = ex is RpcException rpc ? rpc.Status.Detail : ex.Message;
        }
    }

    private bool CanCreate => !IsBusy && Validation.IsValid;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        if (!Validation.IsValid)
        {
            Validation.ShowAll();
            return;
        }

        IsBusy = true;
        try
        {
            var created = await _api.CreateAccessKeyAsync(Name.Trim(), Lifetime.Days, ReadOnly, CancellationToken.None).ConfigureAwait(true);
            NewKey = created.Secret;
            NewKeyTitle = created.Key.Name;
            Message = null;
            Name = string.Empty;
            ReadOnly = false;
            Validation.Reset();
            Show(await _api.ListAccessKeysAsync(everyUser: false, CancellationToken.None).ConfigureAwait(true));
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.InvalidArgument)
        {
            Validation.SetServerError(nameof(Name), ex.Status.Detail); // the name is the field a refusal names
        }
        catch (RpcException ex)
        {
            MessageIsError = true;
            Message = ex.Status.Detail;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CopyKeyAsync()
    {
        if (NewKey is { } key && _clipboard is not null && await _clipboard.SetTextAsync(key).ConfigureAwait(true))
        {
            MessageIsError = false;
            Message = "Copied.";
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(AccessKeyRow? row)
    {
        if (row is null || !await _dialogs.ConfirmAsync("Delete access key", $"Agents that use {row.Name} can no longer connect.", "Delete").ConfigureAwait(true))
        {
            return;
        }

        try
        {
            Show(await _api.DeleteAccessKeyAsync(row.Id, anyUser: false, CancellationToken.None).ConfigureAwait(true));
            Message = null;
        }
        catch (RpcException ex)
        {
            MessageIsError = true;
            Message = ex.Status.Detail;
        }
    }

    private void Show(IReadOnlyList<AccessKey> keys)
    {
        Keys.ReplaceAll(keys.Select(k => new AccessKeyRow(k))); // one Reset
        OnPropertyChanged(nameof(HasKeys));
    }
}
