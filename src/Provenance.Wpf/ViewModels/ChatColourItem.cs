using System.Windows.Media;
using Provenance.Common.Social;

namespace Provenance.Wpf.ViewModels;

public sealed record ChatColourItem(int Pick, Brush Swatch)
{
    public string Name => ChatLook.ColourName(Pick);
}
