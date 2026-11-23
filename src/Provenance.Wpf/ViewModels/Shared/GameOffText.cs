using Provenance.Common.Hook;

namespace Provenance.Wpf.ViewModels.Shared;

public static class GameOffText
{
    public const string ChatRooms = "You cannot access chat rooms whilst FINAL FANTASY XI is running.";
    public const string GroupChats = "You cannot access group chats whilst FINAL FANTASY XI is running.";
    public const string Profiles = "You cannot view others' profiles whilst FINAL FANTASY XI is running.";
    public const string EditProfile = "You cannot edit your profile whilst FINAL FANTASY XI is running.";
    public const string Messages = HookFailures.Messages.GameOff;
    public const string Status = "You cannot change your online status from Provenance whilst FINAL FANTASY XI is running.";
    public const string FriendList = "You cannot change your friend list from Provenance whilst FINAL FANTASY XI is running.";
}
