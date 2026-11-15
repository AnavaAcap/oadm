using Avalonia.Controls;

namespace Oadm.Client.Settings;

/// <summary>The dialog Access keys (see <see cref="AccessKeysViewModel"/>). View only: wiring to the view model.</summary>
public partial class AccessKeysWindow : Window
{
    public AccessKeysWindow()
    {
        InitializeComponent();
    }

    public void Attach(AccessKeysViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.CloseRequested += OnCloseRequested;
        Closed += (_, _) => viewModel.CloseRequested -= OnCloseRequested;
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();
}
