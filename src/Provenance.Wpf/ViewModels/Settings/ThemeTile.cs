using CommunityToolkit.Mvvm.ComponentModel;

namespace Provenance.Wpf.ViewModels.Settings;

public sealed class ThemeTile : ObservableObject
{
    private readonly Action<int> _pick;
    private bool _picked;

    internal ThemeTile(string theme, string title, int index, Action<int> pick)
    {
        Title = title;
        Index = index;
        Image = $"pack://application:,,,/Provenance;component/Assets/themes/theme-{theme}.png";
        _pick = pick;
    }

    public string Title { get; }
    public int Index { get; }
    public string Image { get; }

    public bool Picked
    {
        get => _picked;
        set
        {
            if (!SetProperty(ref _picked, value) || !value) return;
            _pick(Index);
        }
    }
}
