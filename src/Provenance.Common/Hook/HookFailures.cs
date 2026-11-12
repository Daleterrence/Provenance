namespace Provenance.Common.Hook;

public static class HookFailures
{
    public const string ChatCommError = "Chat communication error.";
    public const string Busy = "PlayOnline is busy with something else. Try again in a moment.";
    public const string FriendListChanged = "Your friend list changed. Try again.";
    public const string IgnoredAlready = "That person is already on your ignore list.";
    public const string FriendListFull = "Your friend list is full (200 friends).";
    public static string ChangeRejected(string reason) => $"PlayOnline didn't accept the change ({reason}).";
    public const string RequestWriteFailed = "Provenance couldn't send the request to PlayOnline. Try again.";
    public const string TooLongForLine = "That's too long for one line.";
    public const string LineHandleChanging = "Your handle was changing, so that line wasn't sent. Try again.";
    public const string LineMayNotHaveGone = "PlayOnline didn't answer, so that line may not have gone.";

    // what a chat line's send says for the failures every chat shares, null for any other
    public static string? ForChatLine(string state) => state switch
    {
        "failed:too long" => TooLongForLine,
        "failed:timeout" => LineMayNotHaveGone,
        "failed:handle changing" => LineHandleChanging,
        "failed:busy" => Busy,
        _ => null,
    };

    public static class Messages
    {
        public const string HandleChanging = "Your handle was changing, so the message wasn't sent. Try again.";
        public const string NoAnswer = "Not sent: PlayOnline didn't answer.";
        public const string Unconfirmed = "Not confirmed: Provenance didn't hear back from PlayOnline. It might still have been sent.";
        public const string Unsent = "Not sent: FINAL FANTASY XI didn't finish sending it.";
        public const string FriendListChanged = "Not sent: the friend list changed.";
        public const string NotAccepted = "Not sent: this person hasn't accepted your friend request yet.";
        public const string ToSelf = "Not sent: you can't send a message to your own handle.";
        public const string TooLong = "Not sent: messages can't be longer than 300 characters, and some characters count as two.";
        public const string WriteFailed = "Not sent: Provenance couldn't send it to PlayOnline.";
        public const string Stopped = "Not sent: PlayOnline didn't take your recent messages, so sending is off until you log in again.";
        public const string HourlyLimit = "Not sent: you've sent 600 messages in the last hour. Try again later.";
        public const string SessionEnded = "Not sent: the PlayOnline session ended before it went.";
        public const string GameStarted = "Not sent: FINAL FANTASY XI started before it went.";
        public const string Returned = "Not delivered: PlayOnline sent it back.";
        public const string Rejected = "Not sent: PlayOnline didn't accept it.";
    }

    public static class Handles
    {
        public const string AlreadyActive = "That handle is already active.";
        public const string LimitText = "Content ID limit reached. Cannot link any more Content IDs.";
        public const string ListChanged = "Your handles or characters changed, so nothing was saved. Try again.";
        public const string AlreadyLinked = "Not saved: that character is already linked to a handle.";
        public const string Reloaded = "Not saved: PlayOnline had a newer Content ID list, so Provenance loaded that instead. Try again.";
        public const string NotSaved = "Not saved: PlayOnline didn't keep the change. Try again.";
        public const string Orphan = "That character is linked to a handle this account doesn't have any more, so it can't be changed.";
        public const string Closing = "That couldn't be done while PlayOnline was closing or FINAL FANTASY XI was starting.";
        public const string Cancelled = "FINAL FANTASY XI started before PlayOnline answered, so Provenance couldn't confirm the change.";
        public const string SwitchNoAnswer = "PlayOnline didn't answer, so your active handle may not have changed.";
        public const string ChangeNoAnswer = "PlayOnline didn't answer, so the change may not have been saved.";
        public const string PreLaunchAlreadyLinked = "Not saved: one of the characters is already linked to a handle.";
        public const string HandleListReloaded = "Not saved: PlayOnline had a newer handle list, so Provenance loaded that instead. Try again.";
        public const string Unconfirmed =
            "An earlier handle change wasn't confirmed, so handles can't be created, deleted or renamed until you log in again.";
        public const string CreateUnconfirmed =
            "PlayOnline didn't confirm it, so the handle may have been created anyway. Log out and back in to see your handles as PlayOnline has them.";
        public const string DeleteUnconfirmed =
            "PlayOnline didn't confirm it, so the handle may still be there. Log out and back in to see your handles as PlayOnline has them.";
        public const string PlayHeld =
            "A handle change wasn't confirmed, and FINAL FANTASY XI could send it again when it starts. Log out and back in to play.";
        public const string PlayDiscards = "Your handle change was not confirmed on the server, starting FINAL FANTASY XI will discard it. Continue?";
        public static string SwitchRejected(string reason) => $"Provenance couldn't confirm the switch ({reason}).";
        public static string ChangeRejected(string reason) => $"Not saved: PlayOnline didn't accept the change ({reason}).";
    }

    public static class ChatRooms
    {
        public const string NotNow = "Chat rooms aren't available right now.";
        public const string MasterOnly = "This command is for the room master only.";
        public const string Unlocked = "The old password was removed, but the new one couldn't be set. The room has no password now.";
        public const string MasterHandleChanging = "Your handle was changing, so nothing was changed. Try again.";
        public const string Refused = "Room entry refused.";
        public const string WrongPassword = "Wrong password.";
        public const string Full = "Selected chat room is currently full and cannot be entered.";
        public const string Closed = "This room is already closed and cannot be entered.";
        public const string JoinHandleChanging = "Your handle was changing, so the room wasn't entered. Try again.";
        public const string BadName = "The entered room name cannot be used. Please enter a different name.";
        public static string AlreadyLeft(string name) => $"{name} has already left.";
        public static string RoomListFailed(string reason) => $"The room list didn't load ({reason}).";
    }

    public static class GroupChat
    {
        public const string Busy = "Unable to join Group Chat. The server is busy.";
        public const string JoinFailed = "Unable to join Group Chat.";
        public const string NotMember = "You are not a member of the selected group.";
        public const string SendFailed = "Unable to send chat message.";
        public const string Characters = "Group chat can't send some of those characters.";
        public const string JoinHandleChanging = "Your handle was changing, so the group chat wasn't joined. Try again.";
        public const string HandlesChanged = "Your handles changed, so the group chat wasn't joined. Try again.";
    }

    public static class Friends
    {
        public const string EditNoAnswer = "PlayOnline didn't answer the change.";
        public const string DeleteOnly = "That person hasn't accepted your friend request yet, so you can only delete it.";
        public const string IgnoreSelf = "You can't ignore yourself.";
        public const string IgnoreListChanged = "Your ignore list changed. Try again.";
        public const string BadName = "Use 1 to 15 English letters, numbers or symbols.";
        public const string EditHandleChanging = "Your handle was changing, so nothing was changed. Try again.";
        public const string EditReloaded = "PlayOnline had a newer friend list, so Provenance loaded it instead. Try again.";
        public const string IgnoreListFull = "Your ignore list is full (100 people).";
        public const string AnswerHandleGone = "This handle does not exist anymore, so the friend request was not received.";
        public const string AnswerNoReply = "PlayOnline didn't reply, so it's not certain your answer got through. If the request's still open, it'll show again next time you log in.";
        public const string AnswerHandleChanging = "Your handle was changing, so your answer wasn't sent. Try again.";
        public const string NoSuchRequest = "That friend request isn't there any more.";
        public const string OwnRequest = "That request is from one of your own handles. You can only decline it.";
        public const string AnswerFriendGone = "That person isn't on your friend list any more, so nothing was sent.";
        public const string AnswerStopped = "PlayOnline didn't take your recent messages, so answers can't be sent until you log in again.";
        public const string AnswerHourlyLimit = "You've sent 600 messages in the last hour, so your answer wasn't sent. Try again later.";
        public const string AnswerListNotSaved = "PlayOnline didn't save the friend list, so your answer wasn't sent. Try again.";
        public const string SearchNoAnswer = "PlayOnline didn't answer the search.";
        public const string BadHandleName = "Type a handle name of up to 15 English letters, numbers or symbols.";
        public const string QuoteInName = "Provenance can't search for handle names with an apostrophe (') in them.";
        public const string BadHid = "Type an H-ID in numbers, like 00-1234567.";
        public const string BadCharacterName = "Type a character name of up to 15 English letters, with no spaces, numbers or symbols.";
        public const string NoSchemaCharacters = "Provenance couldn't read the profile layout from your PlayOnline install, so it can't search for characters.";
        public const string NoSchemaHandles = "Provenance couldn't read the profile layout from your PlayOnline install, so it can't search for handles.";
        public const string NoHidHit = "PlayOnline didn't find a handle with that H-ID.";
        public const string NoCharacterHit = "PlayOnline didn't find a FINAL FANTASY XI character with that name.";
        public const string NoHandleHit = "PlayOnline didn't find a handle with that name.";
        public const string RequestNoAnswer = "PlayOnline didn't answer the friend request.";
        public const string RequestSelf = "You can't add yourself as a friend.";
        public const string NoSuchResult = "Search again, then send the request.";
        public const string RequestStopped = "PlayOnline didn't take your recent messages, so friend requests can't be sent until you log in again.";
        public const string RequestHourlyLimit = "You've sent 600 messages in the last hour, so the request wasn't sent. Try again later.";
        public const string RequestListNotSaved = "PlayOnline didn't save the friend list, so your request wasn't sent. Try again.";
        public const string PictureNoAnswer = "PlayOnline didn't answer the profile picture change.";
        public const string PictureHandleChanging = "Your handle was changing, so the profile picture wasn't saved. Try again.";
        public const string StatusNoAnswer = "PlayOnline didn't answer the status change.";
        public static string AnswerRejected(string reason) => $"PlayOnline didn't accept your answer ({reason}).";
        public static string SearchRejected(string reason) => $"PlayOnline couldn't run the search ({reason}).";
        public static string AlreadyFriend(string name) => $"{name} is already on your friend list.";
        public static string IgnoredFirst(string name) => $"{name} is on your ignore list. Stop ignoring them first.";
        public static string FriendListChangedFor(string name) =>
            $"Your friend list changed before the request to {name} went, so it wasn't sent. Try again.";
        public static string RequestRejected(string reason) => $"PlayOnline didn't accept the request ({reason}).";
        public static string PictureRejected(string reason) => $"PlayOnline didn't save the profile picture ({reason}).";
    }

    public static class Groups
    {
        public const string NoAnswer = "PlayOnline didn't answer, so the group change may not have gone through.";
        public const string HandleGone = "This handle does not exist anymore, so the group invitation was not received.";
        public const string DisbandMasterOnly = "Only the group master can disband the group.";
        public const string HandleChanging = "Your handle was changing, so nothing was done. Try again.";
        public const string NotAllowed = "Only the group master (or a sub-master) can do that.";
        public const string InviteSelf = "You can't invite yourself to a group.";
        public const string OnlyInvited = "You've only been invited to that group. Accept the invitation first.";
        public const string AlreadyIn = "That person is already in this group, or already invited.";
        public const string Full = "This group is full (64 members).";
        public const string TooManyGroups = "You can only be in 4 friend groups.";
        public const string MasterCannotLeave = "The group master can't leave the group. Make someone else master first, or disband it.";
        public const string NeedName = "Type a group name.";
        public const string NameTooLong = "That group name is too long (20 English letters, or 10 Japanese characters).";
        public const string AlreadyMember = "You're already in this group with another handle.";
        public const string Changed = "The group changed. Try again.";
        public const string FriendGone = "Your friend list changed, so the invitation wasn't sent. Try again.";
        public const string NameInUse = "That group name is already in use.";
        public const string NameRejected = "That group name can't be used.";
        public const string Stopped = "PlayOnline didn't take your recent messages, so invitations can't be sent or answered until you log in again.";
        public const string HourlyLimit = "You've sent 600 messages in the last hour. Try again later.";
    }

    public static class Profile
    {
        public const string NoAnswer = "PlayOnline didn't answer, so the profile couldn't be shown.";
        public const string None = "This person doesn't have a PlayOnline profile to show.";
        public const string FriendsOnly = "You can only view your friends' profiles.";
        public const string NoSchema = "Provenance couldn't read the profile layout from your PlayOnline install, so it can't show profiles.";
        public const string NoCharacter = "Provenance couldn't find that character. Open the profile again from your friends list.";
        public static string NotSent(string reason) => $"PlayOnline didn't send the profile ({reason}).";
        public static string NotAsked(string reason) => $"Provenance couldn't ask PlayOnline for the profile ({reason}).";
    }

    public static class ProfileEditor
    {
        public const string Cancelled = "Logging out or starting FINAL FANTASY XI stopped the request.";
        public const string LoadHandleChanging = "Your handle was changing, so the profile wasn't loaded. Open the profile again.";
        public const string SaveHandleChanging = "Your handle was changing, so the profile wasn't saved. Open the profile again.";
        public const string LoadNoAnswer = "PlayOnline didn't answer, so your profile couldn't be loaded.";
        public const string NoSchema = "Provenance couldn't read the profile layout from your PlayOnline install, so it can't show your profile.";
        public const string NoProfile = "PlayOnline doesn't have a profile for this handle.";
        public const string NotLoaded = "Open the profile again before saving.";
        public const string NothingToSave = "You haven't changed anything, so there's nothing to save.";
        public const string BadMail = "PlayOnline doesn't accept that mail address.";
        public const string MailTooLong = "The mail address is too long.";
        public const string BadName = "PlayOnline doesn't accept that handle name. Use English letters, numbers and symbols.";
        public const string BadWord = "That handle name has a word in it that PlayOnline doesn't allow. Choose a different name.";
        public const string NameRejected = "The entered name could not be registered. Please register with a different name.";
        public const string NameNotKept = "PlayOnline didn't keep the new handle name. Try again later.";
        public const string MismatchHandle = "PlayOnline sent a profile that isn't this handle's, so nothing was changed.";
        public const string WriteFailed = "Provenance couldn't keep the profile PlayOnline sent. Try again.";
        public const string SaveUnconfirmed = "PlayOnline didn't confirm the save. Open your profile again to see what it kept.";
        public const string SessionEnded = "Your PlayOnline session ended, so the request stopped.";
        public const string NotAllowed = "PlayOnline said this account isn't allowed to make that change.";
        public static string LoadFailed(string why) => $"PlayOnline didn't send your profile{why}";
        public static string RenameFailed(string why) => $"PlayOnline didn't rename the handle{why}";
        public static string SaveFailed(string why) => $"PlayOnline didn't save your profile{why}";
    }

    public static string AshitaExampleFile(string name) =>
        $"{name} is one of Ashita's example files, so Provenance won't change it. Duplicate it under Windower/Ashita and change the copy instead.";

    public const string AshitaExampleCard =
        "This is one of Ashita's example files, which Provenance doesn't change. Duplicate it to make one of your own.";
}
