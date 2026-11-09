using Provenance.Common.Hook;
using Provenance.Common.PolFormat;

namespace Provenance.Common.Login;

public static class HookExitText
{
    public const string DetailsAgainText = "Provenance needs this account's login details again. Press Edit account to add them.";

    // POL's password box stops at 16 and polcore has no room for more, a longer one gets refused before pol.exe sees it
    public const int MaxPolPasswordLength = 16;
    public const string PolPasswordTooLongText =
        "Your saved PlayOnline password is longer than 16 characters, which PlayOnline doesn't allow. Press Edit account and enter it again.";
    public const string PlayUnreadableText = "Provenance couldn't start the game. Close Provenance and open it again.";
    public const string PlayRefusedText = "PlayOnline didn't let the game start. Log in again and try once more.";
    public const string PlaySessionClosedText = "Your PlayOnline session closed before the game could start. Log in again and try once more.";
    public const string PrivateFolderText = "Provenance couldn't use its own folder. Restart Provenance and try again.";
    public const string UnsafeStartText = "Provenance couldn't start PlayOnline safely. Restart Provenance and try again.";
    public const string RefusedText = "PlayOnline didn't accept your login details. Check your PlayOnline ID, passwords and one-time password.";

    public const string GameStartFailedText = "FINAL FANTASY XI couldn't start. Try again, and if it keeps happening, check the game's files with Repair.";
    public const string NoPolHandleText = "This PlayOnline account doesn't have a handle, and none was created.";

    public const string UnfinishedText = "The PlayOnline login didn't finish. Try again.";
    private const string ClosedBeforeLogInText = "PlayOnline closed before the login finished.";

    internal static (int Code, string Text) EndedBeforeLogIn(int? exitCode) => (HookExit?)exitCode switch
    {
        HookExit.AddressesNotFound => (LoginCodes.Unfinished, ViewerBuild.UnsupportedText),
        HookExit.DetailsUnreadable => (LoginCodes.DetailsUnreadable, DetailsAgainText),
        HookExit.NoHandle => (LoginCodes.NoPolHandle, NoPolHandleText),
        HookExit.BadIpcFolder => (LoginCodes.LauncherFailed, PrivateFolderText),
        HookExit.UnsafeStart => (LoginCodes.LauncherFailed, UnsafeStartText),
        HookExit.GameStartFailed => (LoginCodes.LauncherFailed, GameStartFailedText),
        HookExit.Refused => (LoginCodes.AuthFailed, RefusedText),
        HookExit.TurnedAway => (LoginCodes.AccountBlocked, "PlayOnline turned this account away. Log in with the PlayOnline Viewer to see why."),
        HookExit.ListLoadFailed => (LoginCodes.Unfinished, LoadFailedText("")),
        _ => (LoginCodes.Unfinished, ClosedBeforeLogInText),
    };

    internal static string SessionClosedText(int? exitCode) => (HookExit?)exitCode switch
    {
        HookExit.BadIpcFolder => PrivateFolderText,
        HookExit.UnsafeStart => UnsafeStartText,
        HookExit.GameStartFailed => GameStartFailedText,
        _ => "Your PlayOnline session has closed. Log in again to see your friends and messages.",
    };

    // what to tell the player once the game's pol.exe has ended, null for a normal end
    public static string? GameEndedText(int? exitCode) => exitCode == (int)HookExit.GameStartFailed ? GameStartFailedText : null;

    // null for a line that doesn't end the login
    internal static (int Code, string Text)? LoginEnd(HookEvent? e, PolRegion region, int? viewerLanguage) => e switch
    {
        DetailsUnreadable => (LoginCodes.DetailsUnreadable, DetailsAgainText),
        AuthRefused { Reason: { } reason } => (LoginCodes.AuthFailed, PolAccountNotices.LogInRefused(reason, region)),
        AuthRefused => (LoginCodes.AuthFailed, RefusedText),
        AccountBlocked blocked => (LoginCodes.AccountBlocked, PolAccountNotices.Blocked(blocked.Notice, region, viewerLanguage)),
        ListLoadFailed failed => (LoginCodes.Unfinished, LoadFailedText(failed.List)),
        NoHandle => (LoginCodes.NoPolHandle, NoPolHandleText),
        HandleRefused => (LoginCodes.HandleRejected, "PlayOnline didn't accept that handle name. Log in again to try a different one."),
        NoSession => (LoginCodes.Unfinished, UnfinishedText),
        _ => null,
    };

    private static string LoadFailedText(string list) =>
        $"PlayOnline didn't send {list switch { "status" => "your status", "friends" => "your friend list", "handles" => "your handles", "characters" => "your Content ID list", "groups" => "your friend groups", _ => "everything it needs at login" }}, so you've been logged out. Try again in a moment.";
}
