using System.Windows.Input;

using Avalonia.Controls;

using CommunityToolkit.Mvvm.Input;

namespace Oadm.Client.Shell;

/// <summary>The window Access keys of the account menu (see <see cref="AccessKeysViewModel"/>): Close in the footer.</summary>
public partial class AccessKeysWindow : Window
{
    public AccessKeysWindow()
    {
        CloseCommand = new RelayCommand(Close);
        InitializeComponent();
    }

    /// <summary>The footer's Close.</summary>
    public ICommand CloseCommand { get; }
}
