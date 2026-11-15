using System.Collections.Specialized;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Oadm.Client.Assistant;

/// <summary>
/// The view of <see cref="AssistantPanelViewModel"/>. Only input, scrolling and drag plumbing live here: Enter in the message
/// box sends (Shift+Enter a new line), Up arrow in the empty box edits the last message, Escape stops an answer; files
/// pasted or dropped go to the view model; the list follows new content while the user is at its end.
/// </summary>
public partial class AssistantPanelView : UserControl
{
    /// <summary>Within this distance of the end the list counts as at the end and follows new content.</summary>
    public const double FollowDistance = 40;

    private AssistantPanelViewModel? _vm;
    private bool _scrollPending;
    private ScrollViewer? _scroller;

    public AssistantPanelView()
    {
        InitializeComponent();
        MessageBox.AddHandler(KeyDownEvent, OnMessageKeyDown, RoutingStrategies.Tunnel);
        MessageBox.PastingFromClipboard += OnPasting;
        AddHandler(KeyDownEvent, OnPanelKeyDown, RoutingStrategies.Bubble);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
        Conversation.AddHandler(ScrollViewer.ScrollChangedEvent, OnScrollChanged);
    }

    private void OnMessageKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is not { } vm)
        {
            return;
        }

        if (e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Shift) == 0)
        {
            e.Handled = true;
            if (vm.SendCommand.CanExecute(null))
            {
                vm.SendCommand.Execute(null);
            }
        }
        else if (e.Key == Key.Up && e.KeyModifiers == KeyModifiers.None && vm.EditLast())
        {
            e.Handled = true;
        }
    }

    private void OnPanelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _vm is { IsStreaming: true } vm && vm.StopCommand.CanExecute(null))
        {
            e.Handled = true;
            vm.StopCommand.Execute(null);
        }
    }

    // ---- Paste and drop -------------------------------------------------------------------------------------------------

    private void OnPasting(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return; // the text box's own paste
        }

        e.Handled = true;
        _ = PasteAsync(clipboard);
    }

    /// <summary>Files on the clipboard attach, a picture attaches as a PNG, anything else pastes as text.</summary>
    private async Task PasteAsync(IClipboard clipboard)
    {
        try
        {
            var files = await clipboard.TryGetFilesAsync();
            if (files is { Length: > 0 } && _vm is { } vm)
            {
                await vm.AttachFilesAsync(LocalPaths(files));
                return;
            }

            using (var bitmap = await clipboard.TryGetBitmapAsync())
            {
                if (bitmap is not null && _vm is { } target)
                {
                    using var png = new MemoryStream();
                    bitmap.Save(png, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                    await target.AttachPastedImageAsync(png.ToArray(), CancellationToken.None);
                    return;
                }
            }

            var text = await clipboard.TryGetTextAsync();
            if (!string.IsNullOrEmpty(text))
            {
                MessageBox.SelectedText = text; // replaces the selection or inserts at the caret
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // a clipboard the system would not hand out: nothing pasted, like the text box's own paste
        }
    }

    private static List<string> LocalPaths(IEnumerable<IStorageItem> items) => [.. items.Select(i => i.TryGetLocalPath()).OfType<string>()];

    private static bool HasFiles(DragEventArgs e) => e.DataTransfer.Contains(DataFormat.File);

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (_vm is { } vm && HasFiles(e))
        {
            e.DragEffects = DragDropEffects.Copy;
            vm.IsDragOver = true;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        // Leaving a child for another raises a leave too: only a pointer outside the panel ends the hint.
        if (_vm is { } vm && !new Rect(Bounds.Size).Contains(e.GetPosition(this)))
        {
            vm.IsDragOver = false;
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (_vm is not { } vm)
        {
            return;
        }

        vm.IsDragOver = false;
        if (e.DataTransfer.TryGetFiles() is { Length: > 0 } files)
        {
            e.Handled = true;
            _ = vm.AttachFilesAsync(LocalPaths(files));
        }
    }

    // ---- Following the end ----------------------------------------------------------------------------------------------

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.Items.CollectionChanged -= OnItemsChanged;
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.ScrollToEndRequested -= OnScrollToEndRequested;
            _vm.FocusMessageRequested -= OnFocusMessageRequested;
        }

        _vm = DataContext as AssistantPanelViewModel;
        if (_vm is not null)
        {
            _vm.Items.CollectionChanged += OnItemsChanged;
            _vm.PropertyChanged += OnViewModelChanged;
            _vm.ScrollToEndRequested += OnScrollToEndRequested;
            _vm.FocusMessageRequested += OnFocusMessageRequested;
        }
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AssistantPanelViewModel.IsOpen) && _vm is { IsOpen: true })
        {
            Dispatcher.UIThread.Post(() => MessageBox.Focus(), DispatcherPriority.Background);
        }
    }

    private void OnFocusMessageRequested(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        MessageBox.Focus();
        MessageBox.CaretIndex = MessageBox.Text?.Length ?? 0;
    }, DispatcherPriority.Background);

    private void OnScrollToEndRequested(object? sender, EventArgs e) => RequestScrollToEnd();

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && _vm is { Items.Count: > 0 } vm)
        {
            if (vm.IsFollowing)
            {
                RequestScrollToEnd();
            }

            foreach (var answer in e.NewItems?.OfType<AssistantAnswerItem>() ?? [])
            {
                answer.PropertyChanged += (_, _) => FollowEnd(answer);
            }
        }
    }

    /// <summary>
    /// The user scrolled: at the end (or within <see cref="FollowDistance"/> of it) the list follows, else it stays where the
    /// user is. Content that grows (the extent) or a smaller list (the message box grew) never changes it, only a move of
    /// the offset does; while following, they keep the end in view.
    /// </summary>
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_vm is not { } vm || e.Source is not ScrollViewer scroller || _scrollPending)
        {
            return;
        }

        if (e.OffsetDelta.Y == 0)
        {
            if (vm.IsFollowing && (e.ViewportDelta.Y != 0 || e.ExtentDelta.Y != 0))
            {
                RequestScrollToEnd(); // the message box grew (files, more lines) or an entry grew: the end stays in view
            }

            return;
        }

        vm.IsFollowing = scroller.Offset.Y >= scroller.Extent.Height - scroller.Viewport.Height - FollowDistance;
    }

    /// <summary>One scroll per burst of changes (a post only when none waits).</summary>
    private void RequestScrollToEnd()
    {
        if (_scrollPending)
        {
            return;
        }

        _scrollPending = true;
        Dispatcher.UIThread.Post(ScrollToEnd, DispatcherPriority.Background);
    }

    private void ScrollToEnd()
    {
        try
        {
            if (_vm is not { Items.Count: > 0 } vm)
            {
                return;
            }

            Conversation.ScrollIntoView(vm.Items[^1]);
            _scroller ??= Conversation.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            _scroller?.ScrollToEnd();
        }
        finally
        {
            _scrollPending = false;
        }
    }

    /// <summary>A streaming answer grows: the list stays at its end while the answer is the last entry and the user follows.</summary>
    private void FollowEnd(AssistantAnswerItem answer)
    {
        if (_vm is { IsFollowing: true, Items.Count: > 0 } vm && ReferenceEquals(vm.Items[^1], answer))
        {
            RequestScrollToEnd();
        }
    }
}
