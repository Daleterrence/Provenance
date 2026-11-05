using System.Windows.Media;

namespace Provenance.Wpf.ViewModels;

// Style is say, notice (arrivals, leaving, master changes), status (away and back), whisper or mine. Colour is a say
// line's own (null for the default), Emote its face
public sealed record ChatLineItem(long Seq, string Text, string Style, DateTimeOffset When)
{
    public string TimeText => When.ToString("HH:mm");
    public Brush? Colour { get; init; }
    public uint? Argb { get; init; }
    public ImageSource? Emote { get; init; }
    public bool HasColour => Colour is not null;
    public bool HasEmote => Emote is not null;
}
