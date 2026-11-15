using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Oadm.Client.Api;
using Oadm.Client.Dialogs;
using Oadm.Client.Shell;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Collections;

namespace Oadm.Client.Settings;

/// <summary>
/// The card Access keys of the Permissions page (administrators, docs/spec/mcp.md "Access keys"): every user's keys (user, name,
/// created, expires, last used, Read only) with Delete, which ends the key at once.
/// </summary>
public sealed partial class AllAccessKeysViewModel(IOadmApi api, IDialogService dialogs) : ObservableObject, IAccessKeyList
{
    /// <summary>Every key, by user, the newest first (at most 20 per user).</summary>
    public RangeObservableCollection<AccessKeyRow> Keys { get; } = [];

    public bool HasKeys => Keys.Count > 0;

    public bool ShowsUser => true;

    public System.Windows.Input.ICommand DeleteKeyCommand => DeleteCommand;

    public string EmptyText => AccessKeyWords.EmptyAll;

    [ObservableProperty] public partial string? Message { get; private set; }

    public async Task LoadAsync()
    {
        try
        {
            Show(await api.ListAccessKeysAsync(everyUser: true, CancellationToken.None).ConfigureAwait(true));
            Message = null;
        }
        catch (Exception ex) when (ex is RpcException or NotSupportedException)
        {
            Message = ex is RpcException rpc ? rpc.Status.Detail : ex.Message;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(AccessKeyRow? row)
    {
        if (row is null || !await dialogs.ConfirmAsync("Delete access key",
            $"Agents that use the access key {row.Name} of {row.UserName} can no longer connect.", "Delete").ConfigureAwait(true))
        {
            return;
        }

        try
        {
            Show(await api.DeleteAccessKeyAsync(row.Id, anyUser: true, CancellationToken.None).ConfigureAwait(true));
            Message = null;
        }
        catch (RpcException ex)
        {
            Message = ex.Status.Detail;
        }
    }

    private void Show(IReadOnlyList<AccessKey> keys)
    {
        Keys.ReplaceAll(keys.Select(k => new AccessKeyRow(k))); // one Reset
        OnPropertyChanged(nameof(HasKeys));
    }
}
