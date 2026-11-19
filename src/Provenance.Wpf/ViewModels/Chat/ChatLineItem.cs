using System.Windows.Media;

namespace Provenance.Wpf.ViewModels.Chat;

public sealed record ChatLineItem(long Seq, string Text, string Style, DateTimeOffset When)
{
    public string TimeText => When.ToString("HH:mm");
    public Brush? Colour { get; init; }
    public uint? Argb { get; init; }
    public ImageSource? Emote { get; init; }
    public bool HasColour => Colour is not null;
    public bool HasEmote => Emote is not null;
}
