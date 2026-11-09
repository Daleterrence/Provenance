using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common.Feeds;
using Provenance.Wpf.Services;
using Serilog;

namespace Provenance.Wpf.ViewModels.Main;

public partial class NewsViewModel : ObservableObject
{
    private readonly FeedService _feeds;
    private readonly Action<string?> _openLink;
    private readonly DispatcherTimer _slideTimer, _worldsTimer;
    private int _slideFrom = -1;

    public NewsViewModel(FeedService feeds, UiThread ui, Action<string?> openLink)
    {
        _feeds = feeds;
        _openLink = openLink;
        _slideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _slideTimer.Tick += (_, _) => AdvanceNews(+1);
        // POL only rewrites world status every 5 mins anyway
        _worldsTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        _worldsTimer.Tick += async (_, _) => { if (WindowShown) await RefreshIfStaleAsync(); };
        _feeds.RegionChanged += (_, _) => ui.Post(async () =>
        {
            await LoadFeedsAsync(again: true);
            await LoadWorldsAsync();
        });
    }

    public event Action<int>? Slid;

    public ObservableCollection<FeedItem> NewsItems { get; } = new();
    public ObservableCollection<FeedItem> StatusItems { get; } = new();

    [ObservableProperty] private FeedItem? _currentNews;
    [ObservableProperty] private int _currentNewsIndex;
    [ObservableProperty] private bool _feedsLoaded;

    public bool ShowLoadingHint => NewsItems.Count == 0;
    partial void OnFeedsLoadedChanged(bool value) { OnPropertyChanged(nameof(ShowLoadingHint)); OnPropertyChanged(nameof(FeedsHint)); }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FeedsHint))]
    private bool _feedsFailed;

    public string FeedsHint => FeedsFailed ? "PlayOnline news isn't available right now."
                             : FeedsLoaded ? "No news right now." : "Loading PlayOnline news...";

    [RelayCommand]
    private async Task RetryFeeds()
    {
        FeedsFailed = false;
        await LoadFeedsAsync();
    }

    public ObservableCollection<WorldStatus> Worlds { get; } = new();

    [ObservableProperty] private string _worldsSummary = "Checking world status...";
    [ObservableProperty] private string _worldsState = "";
    [ObservableProperty] private string _worldsCheckedText = "";

    internal DateTime _worldsAt = DateTime.MinValue;
    internal DateTime _feedsAt = DateTime.MinValue;
    private bool _worldsLoading, _feedsLoading, _feedsAgain;
    private static readonly TimeSpan WorldsMaxAge = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FeedsMaxAge = TimeSpan.FromMinutes(30);

    public bool WindowShown { get; private set; } = true;

    public void SetWindowShown(bool shown)
    {
        bool was = WindowShown;
        WindowShown = shown;
        if (shown && !was) _ = RefreshIfStaleAsync();
    }

    private async Task RefreshIfStaleAsync()
    {
        if (DateTime.UtcNow - _worldsAt >= WorldsMaxAge) await LoadWorldsAsync();
        if (DateTime.UtcNow - _feedsAt >= FeedsMaxAge) await LoadFeedsAsync();
    }

    public async Task LoadWorldsAsync()
    {
        if (_worldsLoading) return;
        _worldsLoading = true;
        FeedFetch<WorldStatus> fetch;
        try { fetch = await _feeds.LoadWorldsAsync(); }
        finally { _worldsLoading = false; }
        _worldsTimer.Start();
        var list = fetch.Items;
        if (list.Count == 0)
        {
            if (Worlds.Count == 0) { WorldsSummary = "World status isn't available"; WorldsState = ""; }
            return;
        }
        // only a good read counts, a saved copy's tried again on the next tick
        if (!fetch.Stale) _worldsAt = DateTime.UtcNow;
        WorldsCheckedText = "Checked at " + fetch.At!.Value.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
        Worlds.Clear();
        foreach (var w in list) Worlds.Add(w);
        var notUp = list.Where(w => !w.IsUp).ToList();
        bool onlyMaintenance = notUp.All(w => w.Status is "maintenance" or "partial-maintenance");
        WorldsSummary = notUp.Count switch
        {
            0 => $"All {list.Count} worlds up",
            _ when notUp.Count == list.Count => onlyMaintenance ? "All worlds in maintenance" : "Worlds are down",
            1 => $"{notUp[0].Name}: {notUp[0].Label}",
            _ => $"{notUp.Count} worlds {(onlyMaintenance ? "in maintenance" : "not up")}",
        };
        WorldsState = notUp.Count == 0 ? "up" : onlyMaintenance ? "maintenance" : "down";
    }

    [RelayCommand]
    private void OpenServerStatus() => _openLink(_feeds.ServicePageUrl);

    public async Task LoadFeedsAsync(bool again = false)
    {
        // a region change mid-load mustn't get lost
        if (_feedsLoading) { _feedsAgain |= again; return; }
        _feedsLoading = true;
        try
        {
            var (news, status) = await _feeds.LoadAsync();
            if (news.Items.Count + status.Items.Count > 0) _feedsAt = DateTime.UtcNow;
            var newNews = (await _feeds.WithLocalImagesAsync(news.Items.Take(5).ToList())).ToList();
            static string Sig(IEnumerable<FeedItem> items) =>
                string.Join("\n", items.Select(i => $"{i.Url}|{i.Title}|{i.Date:O}|{i.Category}|{i.Badge}"));
            if (newNews.Count + status.Items.Count == 0 && FeedsLoaded) return;
            if (Sig(newNews) != Sig(NewsItems))
            {
                var keepUrl = CurrentNews?.Url;
                NewsItems.Clear();
                foreach (var it in newNews) NewsItems.Add(it);
                var at = keepUrl is null ? 0 : Math.Max(0, newNews.FindIndex(n => n.Url == keepUrl));
                CurrentNewsIndex = at;
                CurrentNews = NewsItems.ElementAtOrDefault(at);
            }
            // four fit without a scroll bar
            var topStatus = status.Items.Take(4).ToList();
            if (Sig(topStatus) != Sig(StatusItems))
            {
                StatusItems.Clear();
                foreach (var it in topStatus) StatusItems.Add(it);
            }
            FeedsLoaded = NewsItems.Count > 0 || StatusItems.Count > 0;
            FeedsFailed = !FeedsLoaded;
            OnPropertyChanged(nameof(ShowLoadingHint));
            OnPropertyChanged(nameof(FeedsHint));
            if (NewsItems.Count > 1) _slideTimer.Start();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Feed load failed");
            FeedsFailed = !FeedsLoaded;
        }
        finally
        {
            _feedsLoading = false;
            if (_feedsAgain) { _feedsAgain = false; _ = LoadFeedsAsync(); }
        }
    }

    private void AdvanceNews(int delta)
    {
        if (NewsItems.Count == 0) return;
        CurrentNewsIndex = ((CurrentNewsIndex + delta) % NewsItems.Count + NewsItems.Count) % NewsItems.Count;
    }

    // forward (the timer, the next arrow, a later dot) slides in from the right, back from the left
    private void RaiseSlid(int now)
    {
        int n = NewsItems.Count, was = _slideFrom;
        if (now < 0) return;   // the pager pushes -1 while the list is rebuilt
        _slideFrom = now;
        if (n < 2 || was < 0 || was >= n || was == now) return;
        Slid?.Invoke(now == (was + 1) % n ? 1 : now == (was - 1 + n) % n ? -1 : Math.Sign(now - was));
    }

    partial void OnCurrentNewsIndexChanged(int value)
    {
        RaiseSlid(value);
        if (value < 0 || value >= NewsItems.Count) return;   // the ListBox pushes -1 while the list's rebuilt
        CurrentNews = NewsItems[value];
        if (NewsItems.Count > 1) { _slideTimer.Stop(); _slideTimer.Start(); }
    }

    [RelayCommand]
    private void NextNews() => AdvanceNews(+1);

    [RelayCommand]
    private void PrevNews() => AdvanceNews(-1);

    [RelayCommand]
    private void OpenCurrentNews()
    {
        if (CurrentNews is { Url: { Length: > 0 } url }) _openLink(url);
    }

    public void HoldSlides(bool hold)
    {
        if (hold) _slideTimer.Stop();
        else if (NewsItems.Count > 1) _slideTimer.Start();
    }

    public void BannerFailed()
    {
        if (CurrentNews is not { ImageUrl: not null } item) return;
        BannerImages.Forget(item.ImageUrl);
        var i = NewsItems.IndexOf(item);
        var plain = item with { ImageUrl = null };
        if (i >= 0) NewsItems[i] = plain;
        CurrentNews = plain;
    }
}
