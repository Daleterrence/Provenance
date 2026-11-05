using Provenance.Common.Social;

namespace Provenance.Wpf.ViewModels.Chat;

public sealed record ChatRoomItem(ChatRoomEntry Room, string Name, string Categories)
{
    public string People => $"{Room.People}/{Room.Capacity}";
    public bool Locked => Room.Locked;
    public bool HasCategories => Categories.Length > 0;
    public string Language => ChatLook.Language(Room.Categories);
    public bool HasLanguage => Language.Length > 0;
}
