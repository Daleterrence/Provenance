using System.Windows;
using System.Windows.Input;
using Provenance.Common.Social;
using Provenance.Wpf.Controls;
using Provenance.Wpf.ViewModels.Chat;
using Provenance.Wpf.ViewModels.Shared;

namespace Provenance.Wpf.Views.Chat;

// the viewer's Create Room form: name, zone, occupancy 2-20, password on or off, and the three category groups with
// Not set first
public partial class CreateRoomDialog : AppWindow
{
    private Func<string, bool>? _confirm;

    internal CreateRoomDialog()
    {
        InitializeComponent();
        for (int n = ChatRoomFiles.MinOccupancy; n <= ChatRoomFiles.MaxOccupancy; n++) OccupancyBox.Items.Add($"{n} members");
        OccupancyBox.SelectedIndex = ChatRoomFiles.DefaultOccupancy - ChatRoomFiles.MinOccupancy;
        OnPasswordSwitch(this, new RoutedEventArgs());
        Loaded += (_, _) => NameBox.Focus();
    }

    // set up but not shown, so a test or the screenshots can fill it in
    internal static CreateRoomDialog For(IReadOnlyList<ChatZone> zones, int zone, IReadOnlyList<ChatCategory> categories,
                                         Func<string, bool>? confirm)
    {
        var dlg = new CreateRoomDialog { _confirm = confirm };
        foreach (var z in zones) dlg.ZoneBox.Items.Add(new Choice<int>(z.Code, z.Name));
        dlg.ZoneBox.SelectedItem = dlg.ZoneBox.Items.Cast<Choice<int>>().FirstOrDefault(c => c.Code == zone);
        void Fill(System.Windows.Controls.ComboBox box, int from)
        {
            box.Items.Add(new Choice<int>(0, "Not set"));
            foreach (var c in categories.Where(c => c.Code >= from && c.Code < from + 100).OrderBy(c => c.Code))
                box.Items.Add(new Choice<int>(c.Code, c.Name));
            box.SelectedIndex = 0;
        }
        Fill(dlg.MembersBox, 100);
        Fill(dlg.PurposeBox, 200);
        Fill(dlg.LanguageBox, 300);
        return dlg;
    }

    public static ChatRoomForm? Prompt(Window? owner, IReadOnlyList<ChatZone> zones, int zone, IReadOnlyList<ChatCategory> categories,
                                       Func<string, bool> confirm)
    {
        var dlg = For(zones, zone, categories, confirm);
        try { return dlg.ShowDialogOver(owner) == true ? dlg.Form : null; }
        finally
        {
            dlg.PasswordBox.Clear();
            dlg.ConfirmBox.Clear();
        }
    }

    internal ChatRoomForm? Form { get; private set; }

    private int Code(System.Windows.Controls.ComboBox box) => (box.SelectedItem as Choice<int>)?.Code ?? 0;
    private string Text(System.Windows.Controls.ComboBox box) => (box.SelectedItem as Choice<int>)?.Text ?? "Not set";

    internal string? Problem()
    {
        bool on = PasswordOn.IsChecked == true;
        if (ZoneBox.SelectedItem is null) return "No zone is selected.\nCannot create room.";
        if (ChatRoomFiles.RoomNameProblem(NameBox.Text) is { } name) return name;
        if (on && ChatRoomFiles.NewPasswordProblem(PasswordBox.Password) is { } pw) return pw;
        if (on && PasswordBox.Password != ConfirmBox.Password) return "The password entered in Room Password(Confirm) does not match.";
        return null;
    }

    private void OnCreate(object sender, RoutedEventArgs e)
    {
        if (TryCreate()) DialogResult = true;
    }

    internal bool TryCreate()
    {
        if (Problem() is { } problem)
        {
            ErrorText.Text = problem;
            ErrorText.Visibility = Visibility.Visible;
            return false;
        }
        bool on = PasswordOn.IsChecked == true;
        int occupancy = OccupancyBox.SelectedIndex + ChatRoomFiles.MinOccupancy;
        var question = ChatRoomsViewModel.CreateQuestion(Text(ZoneBox), occupancy, on, Text(MembersBox), Text(PurposeBox),
                                                         Text(LanguageBox));
        if (_confirm?.Invoke(question) == false) return false;
        Form = new ChatRoomForm(Code(ZoneBox), NameBox.Text.Trim(), occupancy, on ? PasswordBox.Password : null, Code(MembersBox),
                                Code(PurposeBox), Code(LanguageBox));
        return true;
    }

    private void OnPasswordSwitch(object sender, RoutedEventArgs e)
    {
        if (PasswordBox is null || ConfirmBox is null) return;
        bool on = PasswordOn.IsChecked == true;
        PasswordBox.IsEnabled = ConfirmBox.IsEnabled = on;
        if (!on)
        {
            PasswordBox.Clear();
            ConfirmBox.Clear();
        }
        OnChanged(sender, e);
    }

    private void OnChanged(object sender, RoutedEventArgs e)
    {
        if (ErrorText is not null) ErrorText.Visibility = Visibility.Collapsed;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
