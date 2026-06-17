using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Gomp.App.ViewModels;

namespace Gomp.App.Views;

public partial class MainWindow : Window
{
    private ScrollViewer? _chatScroll;
    private MainWindowViewModel? _vm;
    private RoomViewModel? _watchedRoom;

    public MainWindow()
    {
        InitializeComponent();
        _chatScroll = this.FindControl<ScrollViewer>("ChatScroll");
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as MainWindowViewModel;
        if (_vm is not null)
            _vm.PropertyChanged += OnVmPropertyChanged;
        WatchSelectedRoom();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedRoom))
            WatchSelectedRoom();
    }

    // Follow the selected room's message log and keep the chat pinned to the
    // bottom as posts land (the usual chat affordance).
    private void WatchSelectedRoom()
    {
        if (_watchedRoom is not null)
            _watchedRoom.Messages.CollectionChanged -= OnMessagesChanged;
        _watchedRoom = _vm?.SelectedRoom;
        if (_watchedRoom is not null)
        {
            _watchedRoom.Messages.CollectionChanged += OnMessagesChanged;
            ScrollChatToEnd();
        }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)
            ScrollChatToEnd();
    }

    private void ScrollChatToEnd() =>
        Dispatcher.UIThread.Post(() => _chatScroll?.ScrollToEnd(), DispatcherPriority.Background);

    private void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            return;

        // The composer's DataContext is the shell VM (it binds the room via
        // SelectedRoom.Draft), so reach the room through the selection, not the
        // TextBox's own context.
        if (DataContext is MainWindowViewModel { SelectedRoom: { } room } && room.SendCommand.CanExecute(null))
            room.SendCommand.Execute(null);
        e.Handled = true;
    }

    private async void OnCopySelf(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel { SelfAddress: { Length: > 0 } addr }
            && GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(addr);
        }
    }

    private async void OnCopyRoomAddress(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel { Manage.RoomAddress: { Length: > 0 } addr }
            && GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(addr);
        }
    }
}
