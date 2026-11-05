using System.Windows.Media;

namespace Provenance.Wpf.ViewModels.Chat;

public sealed record ChatEmoteItem(int Index, ImageSource Image)
{
    public string Name => $"Emoticon {Index}";
}
