using Avalonia.Controls;

using Oadm.Client.Shell;

namespace Oadm.Client.Controls;

/// <summary>
/// The one grid of access keys (<see cref="IAccessKeyList"/>): the account menu's Access keys and the Permissions page's card.
/// The User column shows only for every user's keys (<see cref="IAccessKeyList.ShowsUser"/>).
/// </summary>
public partial class AccessKeyGrid : UserControl
{
    public AccessKeyGrid() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        KeysGrid.Columns[0].IsVisible = DataContext is IAccessKeyList { ShowsUser: true }; // a column is no control: set here, not bound
    }
}
