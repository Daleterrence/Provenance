using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Provenance.Wpf.Controls;
using Provenance.Wpf.ViewModels.Dialogs;

namespace Provenance.Wpf.Views.Dialogs;

// Asked after log in is clicked. The code only ever lives in memory for that one launch
public partial class OtpDialog : AppWindow
{
    private readonly TextBox[] _boxes = new TextBox[6];

    internal OtpPromptViewModel Prompt { get; }

    internal OtpDialog(string source, string item, string account, string reuseKey, string squareEnixId)
    {
        InitializeComponent();
        Prompt = new OtpPromptViewModel(source, item, account, reuseKey, squareEnixId, () => IsVisible);
        DataContext = Prompt;
        Prompt.Fill += digits => Fill(digits, 0);
        Prompt.FocusMaster += () => MasterBox.Focus();
        Prompt.ClearBoxes += () => { foreach (var b in _boxes) b.Text = ""; };
        Prompt.FocusFirstBox += () => _boxes[0].Focus();
        Prompt.FocusEmptyBox += () => (_boxes.FirstOrDefault(b => b.Text.Length == 0) ?? _boxes[0]).Focus();
        Prompt.Finished += _ => DialogResult = true;
        for (int i = 0; i < _boxes.Length; i++)
        {
            var box = new TextBox
            {
                Width = 42, Height = 52, Margin = new Thickness(3, 0, 3, 0), MaxLength = 1,
                FontSize = 24, HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0),
                Style = (Style)FindResource("OutlinedField"), Tag = i,
            };
            MaterialDesignThemes.Wpf.TextFieldAssist.SetCharacterCounterVisibility(box, Visibility.Collapsed);
            System.Windows.Automation.AutomationProperties.SetName(box, $"Digit {i + 1} of 6");
            box.PreviewTextInput += OnDigitInput;
            box.PreviewKeyDown += OnDigitKey;
            box.GotKeyboardFocus += (_, _) => box.SelectAll();
            box.TextChanged += (_, _) => Prompt.ClearError();
            // Ctrl+V skips PreviewTextInput
            DataObject.AddPastingHandler(box, (_, e) =>
            {
                e.CancelCommand();
                if (e.DataObject.GetDataPresent(DataFormats.UnicodeText))
                {
                    var text = (string)e.DataObject.GetData(DataFormats.UnicodeText);
                    Fill(text, text.Count(char.IsAsciiDigit) == 6 ? 0 : (int)box.Tag);
                }
            });
            _boxes[i] = box;
            Digits.Children.Add(box);
        }
        Loaded += (_, _) =>
        {
            _boxes[0].Focus();
            Prompt.Start();
        };
        Closing += (_, _) => Prompt.Close();
    }

    // reuseKey (the SE ID) names the authenticator, so one code never goes out twice
    public static string? Ask(Window? owner, string source = "", string item = "", string reuseKey = "", string squareEnixId = "",
                              string account = "")
    {
        var dlg = new OtpDialog(source, item, account, reuseKey, squareEnixId);
        return dlg.ShowDialogOver(owner) == true ? dlg.Prompt.Code : null;
    }

    private string Code => string.Concat(_boxes.Select(b => b.Text));

    internal static string DigitsFor(string text, int from) =>
        new(text.Where(char.IsAsciiDigit).Take(Math.Max(0, 6 - from)).ToArray());

    private void Fill(string text, int from)
    {
        var digits = DigitsFor(text, from);
        for (int i = 0; i < digits.Length; i++) _boxes[from + i].Text = digits[i].ToString();
        int next = Math.Min(from + digits.Length, _boxes.Length - 1);
        _boxes[next].Focus();
        if (digits.Length == 0) Prompt.ShowError("There's no code on the clipboard.");
        else Prompt.ClearError();
        // a whole code pasted in has no typing to get wrong, so it goes straight away
        if (from == 0 && digits.Length == _boxes.Length) _ = Prompt.SubmitAsync(Code);
    }

    private void OnDigitInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = true;
        if (sender is not TextBox box || e.Text.Length == 0 || !char.IsAsciiDigit(e.Text[0])) return;
        int i = (int)box.Tag;
        box.Text = e.Text[..1];
        if (i < _boxes.Length - 1) _boxes[i + 1].Focus();
    }

    private void OnDigitKey(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        int i = (int)box.Tag;
        switch (e.Key)
        {
            case Key.Back:
                e.Handled = true;
                if (box.Text.Length > 0) box.Text = "";
                else if (i > 0) { _boxes[i - 1].Text = ""; _boxes[i - 1].Focus(); }
                break;
            case Key.Delete:
                e.Handled = true;
                box.Text = "";
                break;
            case Key.Left when i > 0:
                e.Handled = true;
                _boxes[i - 1].Focus();
                break;
            case Key.Right when i < _boxes.Length - 1:
                e.Handled = true;
                _boxes[i + 1].Focus();
                break;
            case Key.Enter:
                e.Handled = true;   // or the default OK button submits a second time
                _ = Prompt.SubmitAsync(Code);
                break;
        }
    }

    private void OnRetry(object sender, RoutedEventArgs e) => Prompt.Retry();

    private void OnUnlock(object sender, RoutedEventArgs e)
    {
        var pw = MasterBox.Password;
        MasterBox.Clear();
        Prompt.Unlock(pw);
    }

    private void OnMasterKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;   // not the dialog's OK
        OnUnlock(sender, e);
    }

    private void OnOk(object sender, RoutedEventArgs e) => _ = Prompt.SubmitAsync(Code);

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnPaste(object sender, RoutedEventArgs e)
    {
        foreach (var b in _boxes) b.Text = "";
        string text = "";
        try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); } catch { }   // another program may hold the clipboard
        Fill(text, 0);
    }
}
