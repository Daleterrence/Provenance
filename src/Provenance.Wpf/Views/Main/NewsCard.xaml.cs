using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Provenance.Wpf.ViewModels.Main;

namespace Provenance.Wpf.Views.Main;

public partial class NewsCard : UserControl
{
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
        if (e.OldValue is MainViewModel old) old.News.Slid -= AnimateNews;
        if (e.NewValue is MainViewModel @new) @new.News.Slid += AnimateNews;
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
