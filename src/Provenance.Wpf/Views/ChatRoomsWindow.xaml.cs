using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Provenance.Common.Social;
using Provenance.Wpf.ViewModels;

namespace Provenance.Wpf.Views;

// unowned like the friends window, so it stays up with the main window in the tray. Closing it leaves the room
public partial class ChatRoomsWindow : Window
{
    private const string DialogTitle = "PlayOnline Chat";
    private readonly ChatRoomsViewModel _vm;

    public ChatRoomsWindow(SocialViewModel social, Func<string, string, bool> savePassword, Action dropPassword)
    {
        InitializeComponent();
        WindowFrame.Attach(this);
        DataContext = _vm = new ChatRoomsViewModel(social, savePassword, dropPassword)
        {
            AskChatName = initial => InputDialog.Prompt(this, DialogTitle, "Enter chat name.", initial,
                                                        maxLength: ChatRoomFiles.MaxNameBytes, validate: ChatRoomFiles.NameProblem),
            AskPassword = () => InputDialog.PromptSecret(this, "Enter Room Password", "Enter password.",
                                                         ChatRoomFiles.MaxPasswordLength, ChatRoomFiles.PasswordProblem),
            ConfirmLeave = question => MessageDialog.Confirm(this, DialogTitle, question, "LEAVE") is not null,
            ShowError = text => MessageDialog.ShowError(this, DialogTitle, text),
        };
        _vm.Lines.CollectionChanged += OnLinesChanged;
        Loaded += (_, _) => _vm.Start();
        Closed += (_, _) =>
        {
            _vm.Lines.CollectionChanged -= OnLinesChanged;
            _vm.Dispose();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || _vm.InRoom) return;
            e.Handled = true;
            Close();
        };
    }

    public static ChatRoomsWindow? Current => Application.Current.Windows.OfType<ChatRoomsWindow>().FirstOrDefault();

    public static void ShowFor(SocialViewModel social, Func<string, string, bool> savePassword, Action dropPassword)
    {
        if (Current is { } open)
        {
            if (!open.IsVisible) open.Show();
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }
        new ChatRoomsWindow(social, savePassword, dropPassword).Show();
    }

    // the session's going, so the room goes with it and no PART is sent
    public void CloseQuietly()
    {
        _vm.CloseQuietly();
        Close();
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            Dispatcher.InvokeAsync(LineScroll.ScrollToEnd, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnRoomDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(RoomList, d) is ListBoxItem
            && _vm.EnterCommand.CanExecute(null))
            _vm.EnterCommand.Execute(null);
    }

    private void OnComposeKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (_vm.SendCommand.CanExecute(null)) _vm.SendCommand.Execute(null);
    }

    // a line stops at 300 bytes of Shift-JIS, a paste gets cut to what fits
    private static string WithoutSelection(TextBox box) => box.Text.Remove(box.SelectionStart, box.SelectionLength);

    private void OnComposeInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is TextBox box && PolMessage.TextBytes(WithoutSelection(box).Insert(box.SelectionStart, e.Text)) > ChatRoomFiles.MaxTextBytes)
            e.Handled = true;
    }

    private void OnComposePaste(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not TextBox box || e.SourceDataObject.GetData(DataFormats.UnicodeText) is not string text) return;
        text = text.Replace("\r", "").Replace('\n', ' ');
        int room = ChatRoomFiles.MaxTextBytes - PolMessage.TextBytes(WithoutSelection(box));
        e.CancelCommand();
        box.SelectedText = PolMessage.FitBytes(text, Math.Max(0, room));
        box.CaretIndex = box.SelectionStart + box.SelectionLength;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
}
