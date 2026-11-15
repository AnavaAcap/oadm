using System.Collections.Specialized;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Oadm.Client.Assistant;

/// <summary>
/// The view of <see cref="AssistantPanelViewModel"/>. Only input and scrolling live here: Enter in the message box sends
/// (Shift+Enter a new line), and the list follows the newest entry.
/// </summary>
public partial class AssistantPanelView : UserControl
{
    private AssistantPanelViewModel? _vm;

    public AssistantPanelView()
    {
        InitializeComponent();
        MessageBox.AddHandler(KeyDownEvent, OnMessageKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnMessageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Shift) == 0 && _vm is { } vm)
        {
            e.Handled = true;
            if (vm.SendCommand.CanExecute(null))
            {
                vm.SendCommand.Execute(null);
            }
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.Items.CollectionChanged -= OnItemsChanged;
            _vm.PropertyChanged -= OnViewModelChanged;
        }

        _vm = DataContext as AssistantPanelViewModel;
        if (_vm is not null)
        {
            _vm.Items.CollectionChanged += OnItemsChanged;
            _vm.PropertyChanged += OnViewModelChanged;
        }
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AssistantPanelViewModel.IsOpen) && _vm is { IsOpen: true })
        {
            Dispatcher.UIThread.Post(() => MessageBox.Focus(), DispatcherPriority.Background);
        }
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && _vm is { Items.Count: > 0 } vm)
        {
            var last = vm.Items[^1];
            Dispatcher.UIThread.Post(() => ScrollToEnd(last), DispatcherPriority.Background);
            if (last is AssistantAnswerItem answer)
            {
                answer.PropertyChanged += (_, _) => FollowEnd(answer);
            }
        }
    }

    private void ScrollToEnd(AssistantItem last)
    {
        Conversation.ScrollIntoView(last);
        Conversation.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()?.ScrollToEnd();
    }

    /// <summary>A streaming answer grows: the list stays at its end while the answer is the last entry.</summary>
    private void FollowEnd(AssistantAnswerItem answer)
    {
        if (_vm is { Items.Count: > 0 } vm && ReferenceEquals(vm.Items[^1], answer))
        {
            Dispatcher.UIThread.Post(() => ScrollToEnd(answer), DispatcherPriority.Background);
        }
    }
}
