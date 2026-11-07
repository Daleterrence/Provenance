using System.Windows;
using Provenance.Common.Login;
using Provenance.Common.Social;
using Provenance.Wpf.ViewModels.Chat;
using Provenance.Wpf.ViewModels.Shared;
using Provenance.Wpf.Views.Chat;
using Provenance.Wpf.Views.Dialogs;

namespace Provenance.Wpf.Services;

// Destructive and SafeKeys put Enter and Esc on cancel so a stray key can't do it, and only Destructive goes red
public enum ConfirmStyle { Normal, Destructive, SafeKeys }

// everything a view model asks the player, answered by whichever window is in front
public interface IDialogs
{
    void Info(string title, string body);
    void Error(string title, string body, bool offerLogs = false);
    bool Confirm(string title, string body, string ok, ConfirmStyle style = ConfirmStyle.Normal, string? cancel = null);
    // null when cancelled, else 0 or 1 for the choice picked
    int? Choose(string title, string body, string ok, string first, string second, ConfirmStyle style = ConfirmStyle.Normal);
    // the trimmed text, null when cancelled. validate gives the problem shown under the box, null when it's fine
    string? Ask(string title, string prompt, string initial = "", int maxLength = 64, bool allowEmpty = false,
                Func<string, string?>? validate = null);
    // the password as typed, null when cancelled
    string? AskSecret(string title, string prompt, int maxLength = 64, Func<string, string?>? validate = null);
    string? PickFolder(string title);
    string? PickFile(string title, string filter, string folder);
    AccountDialogResult? AskAccount(string? accountName, bool useOtp, bool edit, string otpSource = "", string otpItem = "",
                                    bool requireAll = false, string polId = "", string squareEnixId = "", string otpAccount = "");
    string? AskOtp(string source, string item, string reuseKey, string squareEnixId, string account);
    bool AskAgreement(IReadOnlyDictionary<int, string>? strings);
    ChatRoomForm? AskRoom(IReadOnlyList<ChatZone> zones, int zone, IReadOnlyList<ChatCategory> categories, Func<string, bool> confirm);
}

public static class DialogsExtensions
{
    // a menu item's question, with Enter and Esc on cancel
    public static bool Confirm(this IDialogs dialogs, MenuConfirm q) =>
        dialogs.Confirm(q.Title, q.Body, q.Ok, ConfirmStyle.Destructive);
}

// the active window owns the question, else the main window, else it's centred on the screen
public sealed class WpfDialogs : IDialogs
{
    public static readonly IDialogs Default = new WpfDialogs();

    internal static Window? Owner() =>
        Application.Current is { } app ? app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? app.MainWindow : null;

    public void Info(string title, string body) => MessageDialog.ForNotice(title, body).ShowDialogOver(Owner());

    public void Error(string title, string body, bool offerLogs = false) =>
        MessageDialog.ForNotice(title, body, offerLogs).ShowDialogOver(Owner());

    public bool Confirm(string title, string body, string ok, ConfirmStyle style = ConfirmStyle.Normal, string? cancel = null) =>
        MessageDialog.ForConfirm(title, body, ok, style, cancel).ShowDialogOver(Owner()) == true;

    public int? Choose(string title, string body, string ok, string first, string second, ConfirmStyle style = ConfirmStyle.Normal) =>
        MessageDialog.Choose(Owner(), title, body, ok, first, second, style);

    public string? Ask(string title, string prompt, string initial = "", int maxLength = 64, bool allowEmpty = false,
                       Func<string, string?>? validate = null) =>
        InputDialog.Prompt(Owner(), title, prompt, initial, maxLength, allowEmpty, validate);

    public string? AskSecret(string title, string prompt, int maxLength = 64, Func<string, string?>? validate = null) =>
        InputDialog.PromptSecret(Owner(), title, prompt, maxLength, validate);

    public string? PickFolder(string title)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = title };
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    public string? PickFile(string title, string filter, string folder)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            Filter = filter,
            InitialDirectory = System.IO.Directory.Exists(folder) ? folder : "",
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public AccountDialogResult? AskAccount(string? accountName, bool useOtp, bool edit, string otpSource = "", string otpItem = "",
                                           bool requireAll = false, string polId = "", string squareEnixId = "", string otpAccount = "") =>
        AccountDialog.Show(Owner(), accountName, useOtp, edit, otpSource, otpItem, requireAll, polId, squareEnixId, otpAccount);

    public string? AskOtp(string source, string item, string reuseKey, string squareEnixId, string account) =>
        OtpDialog.Ask(Owner(), source, item, reuseKey, squareEnixId, account);

    public bool AskAgreement(IReadOnlyDictionary<int, string>? strings) => AgreementDialog.Ask(Owner(), strings);

    public ChatRoomForm? AskRoom(IReadOnlyList<ChatZone> zones, int zone, IReadOnlyList<ChatCategory> categories,
                                 Func<string, bool> confirm) =>
        CreateRoomDialog.Prompt(Owner(), zones, zone, categories, confirm);
}
