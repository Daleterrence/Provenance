using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Provenance.Wpf.ViewModels.Main;

public enum NoticeTarget { None, Chat, Friends, GroupChat }

public sealed record Notice(string Text, string Icon, NoticeTarget Target, string? Sender, int Request, bool Stays)
{
    public bool Opens => Target != NoticeTarget.None;
}

public partial class NoticeBanner : ObservableObject
{
    private DispatcherTimer? _timer;

    [ObservableProperty] private Notice? _current;

    public void Show(string text, string icon = "mail", NoticeTarget target = NoticeTarget.None, string? sender = null,
                     int request = 0, bool stays = false)
    {
        Current = new Notice(text, icon, target, sender, request, stays);
        if (_timer is null)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            _timer.Tick += (_, _) =>
            {
                Current = null;
                _timer?.Stop();
            };
        }
        _timer.Stop();
        if (!stays) _timer.Start();
    }

    [RelayCommand]
    private void Dismiss() => Current = null;

    public Notice? Take()
    {
        var notice = Current;
        Current = null;
        return notice;
    }

    public void Hold(bool hold)
    {
        if (_timer is null || Current is not { Stays: false }) return;
        if (hold) _timer.Stop();
        else _timer.Start();
    }

    public void DropRequest()
    {
        if (Current is { Request: not 0 }) Current = null;
    }

    public void Drop(Notice? notice)
    {
        if (notice is not null && ReferenceEquals(Current, notice)) Current = null;
    }

    public void DropIfAnswered(Func<int, bool> isWaiting)
    {
        if (Current is { Request: not 0 } n && !isWaiting(n.Request)) Current = null;
    }
}
