using CommunityToolkit.Mvvm.ComponentModel;
using Provenance.Common.Otp;

namespace Provenance.Wpf.ViewModels.Dialogs;

public enum OtpStep { Typing, Fetching, WaitingForNextCode, Installing, Problem, NeedsPassword, Accepting }

public sealed partial class OtpPromptViewModel : ObservableObject
{
    private readonly string _source, _reuseKey;
    private readonly Func<bool> _isShowing;
    private string? _code;
    private bool _closed;
    private readonly LatestOnly _fetch = new();
    private OtpFix _fix;
    private string? _installTool, _problemLabel;
    private Task _inFlight = Task.CompletedTask;

    internal Func<string?, CancellationToken, Task<OtpFetchResult>> Fetch { get; set; }
    internal Func<string, CancellationToken, Task<ToolInstall>> Install { get; set; } = (tool, ct) => ToolInstaller.InstallAsync(tool, null, ct);
    internal Func<string?> OpenSettings { get; set; } = ToolFix.OpenSettings;
    internal Func<string, string, Task<bool>> WasUsed { get; set; } = OtpReuse.WasUsedAsync;
    internal Func<string, string, Task<string?>> MarkUsed { get; set; } = OtpReuse.MarkUsedAsync;
    internal Func<string, Task> Unmark { get; set; } = OtpReuse.UnmarkAsync;
    internal Func<int, CancellationToken, Task> WaitForNextCode { get; set; } = (seconds, ct) => Task.Delay(TimeSpan.FromSeconds(seconds), ct);

    public event Action<string>? Fill;
    public event Action? ClearBoxes;
    public event Action? FocusFirstBox;
    public event Action? FocusEmptyBox;
    public event Action? FocusMaster;
    public event Action<string>? Finished;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(PanelVisible), nameof(MasterVisible), nameof(RetryLabel))] private OtpStep _step;
    [ObservableProperty] private string _sourceText = "";
    [ObservableProperty] private bool _spinnerVisible;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanSubmit))] private bool _checking;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ErrorVisible))] private string? _errorText;

    public bool CanSubmit => !Checking;

    public bool PanelVisible => Step != OtpStep.Typing;

    public bool MasterVisible => Step == OtpStep.NeedsPassword;

    public string? RetryLabel => Step == OtpStep.Problem ? _problemLabel : null;

    public bool ErrorVisible => ErrorText is not null;

    public string? Code => _code;

    internal OtpFix Fix => _fix;

    public OtpPromptViewModel(string source, string item, string account, string reuseKey, string squareEnixId, Func<bool> isShowing)
    {
        _source = source;
        _reuseKey = reuseKey;
        _isShowing = isShowing;
        Fetch = (masterPassword, ct) => OtpSources.FetchAsync(source, item, account, squareEnixId, masterPassword, ct);
    }

    private static bool IsSixDigits(string? s) => s is { Length: 6 } && s.All(char.IsAsciiDigit);

    public void Start()
    {
        if (_source.Length > 0) _ = FetchAsync(null);
    }

    public Task SubmitAsync(string typed)
    {
        if (Checking || _code != null) return Task.CompletedTask;
        if (!IsSixDigits(typed))
        {
            ShowError("Enter all 6 digits.");
            FocusEmptyBox?.Invoke();
            return Task.CompletedTask;
        }
        return _inFlight = CheckTypedAsync(typed);
    }

    private async Task CheckTypedAsync(string typed)
    {
        SetChecking(true);
        try
        {
            if (_reuseKey.Length > 0 && await WasUsed(_reuseKey, typed))
            {
                if (_closed) return;
                ShowCodeUsed();
                return;
            }
            await AcceptAsync(typed);
        }
        finally { SetChecking(false); }
    }

    private async Task AcceptFetchedAsync(string code)
    {
        SetChecking(true);
        try { await AcceptAsync(code); }
        finally { SetChecking(false); }
    }

    internal void SetChecking(bool value)
    {
        Checking = value;
        if (value) ErrorText = null;
    }

    internal void ShowCodeUsed()
    {
        ClearBoxes?.Invoke();
        ShowError("That code was just used for another account. Wait for the next one (it can take up to 30 seconds).");
        FocusFirstBox?.Invoke();
    }

    public void ShowError(string text) => ErrorText = text;

    public void ClearError() => ErrorText = null;

    public void Close()
    {
        _closed = true;
        _fetch.Cancel();
    }

    private async Task AcceptAsync(string code)
    {
        if (_closed) return;
        string? recorded = null;
        if (_reuseKey.Length > 0) recorded = await MarkUsed(_reuseKey, code);
        if (_closed)
        {
            if (recorded is not null) await Unmark(recorded);
            return;
        }
        _code = code;
        Finished?.Invoke(code);
    }

    private async Task FetchAsync(string? masterPassword)
    {
        var name = OtpSources.Name(_source);
        Step = OtpStep.Fetching;
        SpinnerVisible = true;
        _installTool = null;
        SourceText = _source == OtpSources.OnePassword ? OnePassword.WaitText : $"Getting the code from {name}...";
        var mine = _fetch.Begin();
        var r = await Fetch(masterPassword, mine);
        for (int tries = 0; r.Code is { } used && _reuseKey.Length > 0 && await WasUsed(_reuseKey, used) && tries < 3; tries++)
        {
            if (!_isShowing() || mine.IsCancellationRequested) return;
            Step = OtpStep.WaitingForNextCode;
            SourceText = $"The code from {name} was just used for another account. Waiting for the next code...";
            try { await WaitForNextCode(OtpReuse.SecondsToNextCode(), mine); }
            catch (OperationCanceledException) { return; }
            r = await Fetch(null, mine);
        }
        if (!_isShowing() || _code != null) return;
        if (mine.IsCancellationRequested) return;
        SpinnerVisible = false;
        if (r.Code is { } code && !(_reuseKey.Length > 0 && await WasUsed(_reuseKey, code)))
        {
            try { await _inFlight.WaitAsync(mine); }
            catch (OperationCanceledException) { return; }
            if (!_isShowing() || _closed || _code != null || mine.IsCancellationRequested) return;
            Fill?.Invoke(code);
            SourceText = $"Got the code from {name}.";
            Step = OtpStep.Accepting;
            _inFlight = AcceptFetchedAsync(code);
            await _inFlight;
            return;
        }
        if (r.Code != null) r = r with { Code = null, Problem = $"{name} kept giving a code that's already been used.", Fix = OtpFix.TryAgain };
        ShowProblem(r.Problem ?? "", r.Fix, r.Tool);
    }

    internal void ShowProblem(string text, OtpFix fix, string? tool = null, string? label = null)
    {
        SpinnerVisible = false;
        SourceText = text;
        _fix = fix;
        _installTool = tool;
        if (fix == OtpFix.MasterPassword)
        {
            Step = OtpStep.NeedsPassword;
            if (_isShowing()) FocusMaster?.Invoke();
            return;
        }
        _problemLabel = label ?? ToolFix.Label(fix);
        Step = OtpStep.Problem;
        OnPropertyChanged(nameof(RetryLabel));
    }

    public void Retry()
    {
        switch (_fix)
        {
            case OtpFix.Install when _installTool is { } tool:
                _ = InstallAsync(tool);
                break;
            case OtpFix.OpenOnePasswordSettings:
                ShowProblem(OpenSettings() ?? SourceText, OtpFix.TryAgain);
                break;
            case OtpFix.TypeInstead:
                Step = OtpStep.Typing;
                FocusFirstBox?.Invoke();
                break;
            default:
                _ = FetchAsync(null);
                break;
        }
    }

    public void Unlock(string masterPassword)
    {
        if (masterPassword.Length > 0) _ = FetchAsync(masterPassword);
    }

    private async Task InstallAsync(string tool)
    {
        var mine = _fetch.Begin();
        Step = OtpStep.Installing;
        SpinnerVisible = true;
        SourceText = ToolTexts.Downloading(tool);
        ToolInstall r;
        try { r = await Install(tool, mine); }
        catch (OperationCanceledException) { return; }
        if (!_isShowing() || _code != null || mine.IsCancellationRequested) return;
        if (r == ToolInstall.Installed)
        {
            _ = FetchAsync(null);
            return;
        }
        if (r == ToolInstall.NotSupported) ShowProblem(ToolTexts.Result(tool, r), OtpFix.TypeInstead);
        else ShowProblem(ToolTexts.Result(tool, r), OtpFix.Install, tool, ToolFix.Label(OtpFix.TryAgain));
    }
}
