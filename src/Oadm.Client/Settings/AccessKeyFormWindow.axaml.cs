using Avalonia.Controls;

namespace Oadm.Client.Settings;

/// <summary>The dialog Create access key (see <see cref="AccessKeyFormViewModel"/>). View only: wiring to the view model.</summary>
public partial class AccessKeyFormWindow : Window
{
    public AccessKeyFormWindow()
    {
        InitializeComponent();
    }

    public void Attach(AccessKeyFormViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
    }

    /// <summary>Focuses the name.</summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        KeyName.Focus();
    }
}
