using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Provenance.Wpf.ViewModels.Shared;

public sealed class StatusLine : ObservableObject
{
    private DispatcherTimer? _timer;

    public string Text { get; private set; } = "";
    public bool IsError { get; private set; }

    public void Show(string text) => Set(text, false);

    public void Fail(string text) => Set(text, true);

    public void Clear() => Set("", false);

    public void Soften() => Set(Text, false);

    public void Set(string text, bool isError) => Set(text, isError, null);

    public void Show(string text, TimeSpan clearAfter) => Set(text, false, clearAfter);

    private void Set(string text, bool isError, TimeSpan? clearAfter)
    {
        _timer?.Stop();
        bool textChanged = Text != text, errorChanged = IsError != isError;
        Text = text;
        IsError = isError;
        if (textChanged) OnPropertyChanged(nameof(Text));
        if (errorChanged) OnPropertyChanged(nameof(IsError));
        if (text.Length == 0 || clearAfter is not { } after) return;
        if (_timer is null)
        {
            _timer = new DispatcherTimer();
            _timer.Tick += (_, _) => Clear();
        }
        _timer.Interval = after;
        _timer.Start();
    }
}
