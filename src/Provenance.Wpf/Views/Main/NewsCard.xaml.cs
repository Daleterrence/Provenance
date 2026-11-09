using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Provenance.Wpf.ViewModels.Main;

namespace Provenance.Wpf.Views.Main;

public partial class NewsCard : UserControl
{
    private int _newsShown = -1;

    public NewsCard()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnNewsHover(object sender, MouseEventArgs e) => Vm?.News.HoldSlides(true);

    private void OnNewsLeave(object sender, MouseEventArgs e)
    {
        if (sender is UIElement { IsKeyboardFocusWithin: true }) return;
        Vm?.News.HoldSlides(false);
    }

    // focus on the Read link holds the article too, so Enter opens the one shown
    private void OnNewsFocus(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is UIElement card) Vm?.News.HoldSlides(card.IsKeyboardFocusWithin || card.IsMouseOver);
    }

    private void OnBannerFailed(object? sender, ExceptionRoutedEventArgs e) => Vm?.News.BannerFailed();

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel old) old.News.PropertyChanged -= OnNewsChanged;
        if (e.NewValue is MainViewModel @new) @new.News.PropertyChanged += OnNewsChanged;
    }

    // forward (the timer, the next arrow, a later dot) slides in from the right, back from the left
    private void OnNewsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(NewsViewModel.CurrentNewsIndex) || sender is not NewsViewModel vm) return;
        int n = vm.NewsItems.Count, now = vm.CurrentNewsIndex, was = _newsShown;
        if (now < 0) return;   // the pager pushes -1 while the list is rebuilt
        _newsShown = now;
        if (n < 2 || was < 0 || was >= n || was == now) return;
        int dir = now == (was + 1) % n ? 1 : now == (was - 1 + n) % n ? -1 : Math.Sign(now - was);
        AnimateNews(dir);
    }

    private void AnimateNews(int dir)
    {
        if (!SystemParameters.ClientAreaAnimation) return;   // Windows "show animations" is off
        if (NewsContent.RenderTransform is not TranslateTransform tt) return;
        var duration = TimeSpan.FromMilliseconds(340);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(80 * dir, 0, duration) { EasingFunction = ease });
        NewsContent.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
    }
}
