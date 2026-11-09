using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Provenance.Common.Otp;
using Provenance.Wpf.Controls;

namespace Provenance.Wpf.Views.Dialogs;

// Asked after sign in is clicked. The code only ever lives in memory for that one launch
public partial class OtpDialog : AppWindow
{
    private string? _code;
    private readonly TextBox[] _boxes = new TextBox[6];
    private readonly string _source, _item, _account, _reuseKey, _squareEnixId;
    private CancellationTokenSource? _fetch;
    private OtpFix _fix;
    private string? _installTool;
    private bool _sending;
    private bool _closed;

    internal static OtpDialog ForTest(string reuseKey) => new("", "", "", reuseKey, "");

    private OtpDialog(string source, string item, string account, string reuseKey, string squareEnixId)
    {
        _squareEnixId = squareEnixId;
        _source = source;
        _item = item;
        _account = account;
        _reuseKey = reuseKey;
        InitializeComponent();
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
            box.TextChanged += (_, _) => ErrorText.Visibility = Visibility.Collapsed;
            // Ctrl+V skips PreviewTextInput
            DataObject.AddPastingHandler(box, (_, e) =>
            {
                e.CancelCommand();
                if (e.DataObject.GetDataPresent(DataFormats.UnicodeText))
                    Fill((string)e.DataObject.GetData(DataFormats.UnicodeText), (int)box.Tag);
            });
            _boxes[i] = box;
            Digits.Children.Add(box);
        }
        Loaded += (_, _) =>
        {
            _boxes[0].Focus();
            if (_source.Length > 0) _ = FetchAsync(null);
        };
        Closing += (_, _) => { _closed = true; _fetch?.Cancel(); };
    }

    // reuseKey (the SE ID) names the authenticator, so one code never goes out twice
    public static string? Ask(Window? owner, string source = "", string item = "", string reuseKey = "", string squareEnixId = "",
                              string account = "")
    {
        var dlg = new OtpDialog(source, item, account, reuseKey, squareEnixId);
        return dlg.ShowDialogOver(owner) == true ? dlg._code : null;
    }

    private string Code => string.Concat(_boxes.Select(b => b.Text));

    private static bool IsSixDigits(string? s) => s is { Length: 6 } && s.All(char.IsAsciiDigit);

    // the reuse check can wait on a key file, so it's on a worker and the dialog stays responsive
    private async Task SubmitAsync()
    {
        if (_sending || _code != null) return;
        var text = Code;
        if (!IsSixDigits(text))
        {
            ShowError("Enter all 6 digits.");
            (_boxes.FirstOrDefault(b => b.Text.Length == 0) ?? _boxes[0]).Focus();
            return;
        }
        Busy = true;
        try
        {
            if (_reuseKey.Length > 0 && await OtpReuse.WasUsedAsync(_reuseKey, text))
            {
                if (_closed) return;
                ShowCodeUsed();
                return;
            }
            await AcceptAsync(text);
        }
        finally { Busy = false; }
    }

    internal bool Busy
    {
        get => _sending;
        set
        {
            _sending = value;
            OkButton.IsEnabled = !value;
            BusyText.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            if (value) ErrorText.Visibility = Visibility.Collapsed;
        }
    }

    // boxes first (each cleared box hides the error), then the error so it stays up
    internal void ShowCodeUsed()
    {
        foreach (var b in _boxes) b.Text = "";
        ShowError("That code was just used for another account. Wait for the next one (it can take up to 30 seconds).");
        _boxes[0].Focus();
    }

    // if cancel got there first nothing's sent, and the record comes off again so a retry isn't called used
    private async Task AcceptAsync(string code)
    {
        if (_closed) return;
        string? recorded = null;
        if (_reuseKey.Length > 0) recorded = await OtpReuse.MarkUsedAsync(_reuseKey, code);
        if (_closed)
        {
            if (recorded is not null) await OtpReuse.UnmarkAsync(recorded);
            return;
        }
        _code = code;
        DialogResult = true;
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
    }

    internal static string DigitsFor(string text, int from) =>
        new(text.Where(char.IsAsciiDigit).Take(Math.Max(0, 6 - from)).ToArray());

    private void Fill(string text, int from)
    {
        var digits = DigitsFor(text, from);
        for (int i = 0; i < digits.Length; i++) _boxes[from + i].Text = digits[i].ToString();
        int next = Math.Min(from + digits.Length, _boxes.Length - 1);
        _boxes[next].Focus();
        if (digits.Length == 0) ShowError("There's no code on the clipboard.");
        else ErrorText.Visibility = Visibility.Collapsed;
    }

    private void OnDigitInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = true;
        if (sender is not TextBox box || e.Text.Length == 0 || !char.IsAsciiDigit(e.Text[0])) return;
        int i = (int)box.Tag;
        box.Text = e.Text[..1];
        if (i < _boxes.Length - 1) _boxes[i + 1].Focus();
        else if (IsSixDigits(Code)) _ = SubmitAsync();
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
                _ = SubmitAsync();
                break;
        }
    }

    // a code fills the boxes and goes straight away, a problem gets shown and the boxes stay open for typing
    private async Task FetchAsync(string? masterPassword)
    {
        var name = OtpSources.Name(_source);
        SourcePanel.Visibility = Visibility.Visible;
        SourceSpinner.Visibility = Visibility.Visible;
        MasterPanel.Visibility = RetryButton.Visibility = Visibility.Collapsed;
        _installTool = null;
        SourceText.Text = _source == OtpSources.OnePassword ? OnePassword.WaitText : $"Getting the code from {name}...";
        _fetch?.Cancel();
        var mine = _fetch = new CancellationTokenSource();
        var r = await OtpSources.FetchAsync(_source, _item, _account, _squareEnixId, masterPassword, mine.Token);
        // same code as the account before (one authenticator), so wait for the next one
        for (int tries = 0; r.Code is { } used && _reuseKey.Length > 0 && await OtpReuse.WasUsedAsync(_reuseKey, used) && tries < 3; tries++)
        {
            if (!IsVisible || mine != _fetch || mine.IsCancellationRequested) return;
            SourceText.Text = $"The code from {name} was just used for another account. Waiting for the next code...";
            try { await Task.Delay(TimeSpan.FromSeconds(OtpReuse.SecondsToNextCode()), mine.Token); }
            catch (OperationCanceledException) { return; }
            r = await OtpSources.FetchAsync(_source, _item, _account, _squareEnixId, null, mine.Token);
        }
        if (!IsVisible || _code != null) return;
        if (mine != _fetch || mine.IsCancellationRequested) return;   // a newer attempt owns the screen
        SourceSpinner.Visibility = Visibility.Collapsed;
        if (r.Code is { } code && !(_reuseKey.Length > 0 && await OtpReuse.WasUsedAsync(_reuseKey, code)))
        {
            while (_sending && !_closed) await Task.Delay(50);   // a typed code already on its way goes first (yes, a poll loop)
            if (!IsVisible || _closed || _code != null || mine != _fetch) return;
            Fill(code, 0);
            SourceText.Text = $"Got the code from {name}.";
            Busy = true;
            try { await AcceptAsync(code); }
            finally { Busy = false; }
            return;
        }
        if (r.Code != null) r = r with { Code = null, Problem = $"{name} kept giving a code that's already been used.", Fix = OtpFix.TryAgain };
        ShowProblem(r.Problem ?? "", r.Fix, r.Tool);
    }

    // one plain line and the button that fixes it. The boxes stay open for typing whatever it says
    internal void ShowProblem(string text, OtpFix fix, string? tool = null, string? label = null)
    {
        SourcePanel.Visibility = Visibility.Visible;
        SourceSpinner.Visibility = MasterPanel.Visibility = Visibility.Collapsed;
        SourceText.Text = text;
        _fix = fix;
        _installTool = tool;
        if (fix == OtpFix.MasterPassword)
        {
            RetryButton.Visibility = Visibility.Collapsed;
            MasterPanel.Visibility = Visibility.Visible;
            if (IsVisible) MasterBox.Focus();
            return;
        }
        RetryButton.Content = label ?? ToolFix.Label(fix);
        RetryButton.Visibility = Visibility.Visible;
    }

    private void OnRetry(object sender, RoutedEventArgs e)
    {
        switch (_fix)
        {
            case OtpFix.Install when _installTool is { } tool:
                _ = InstallAsync(tool);
                break;
            case OtpFix.OpenOnePasswordSettings:
                // the button asks again once 1Password's open
                ShowProblem(ToolFix.OpenSettings() ?? SourceText.Text, OtpFix.TryAgain);
                break;
            case OtpFix.TypeInstead:
                SourcePanel.Visibility = Visibility.Collapsed;
                _boxes[0].Focus();
                break;
            default:
                _ = FetchAsync(null);
                break;
        }
    }

    // a fresh copy goes straight on to the code. Closing the dialog stops the download
    private async Task InstallAsync(string tool)
    {
        _fetch?.Cancel();
        var mine = _fetch = new CancellationTokenSource();
        RetryButton.Visibility = Visibility.Collapsed;
        SourceSpinner.Visibility = Visibility.Visible;
        SourceText.Text = ToolText.Downloading(tool);
        ToolInstall r;
        try { r = await ToolInstaller.InstallAsync(tool, null, mine.Token); }
        catch (OperationCanceledException) { return; }
        if (!IsVisible || _code != null || mine != _fetch || mine.IsCancellationRequested) return;
        if (r == ToolInstall.Installed)
        {
            _ = FetchAsync(null);
            return;
        }
        if (r == ToolInstall.NotSupported) ShowProblem(ToolText.Result(tool, r), OtpFix.TypeInstead);
        else ShowProblem(ToolText.Result(tool, r), OtpFix.Install, tool, ToolFix.Label(OtpFix.TryAgain));
    }

    private void OnUnlock(object sender, RoutedEventArgs e)
    {
        var pw = MasterBox.Password;
        MasterBox.Clear();
        if (pw.Length > 0) _ = FetchAsync(pw);
    }

    private void OnMasterKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;   // not the dialog's OK
        OnUnlock(sender, e);
    }

    private void OnOk(object sender, RoutedEventArgs e) => _ = SubmitAsync();

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    // fills the boxes but doesn't send, so the user sees the code first
    private void OnPaste(object sender, RoutedEventArgs e)
    {
        foreach (var b in _boxes) b.Text = "";
        string text = "";
        try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); } catch { }   // another program may hold the clipboard
        Fill(text, 0);
    }
}
