namespace Provenance.Common.Login;

// DetailsUnreadable is the hook failing to read the saved entries and Unfinished a login that stopped short, neither one
// a POL refusal
public static class LoginCodes
{
    public const int AuthFailed = -2, NoPolHandle = -3, LauncherFailed = -5, Stalled = -6,
                     HandleRejected = -7, Cancelled = -8, AccountBlocked = -10, DetailsUnreadable = -11, Unfinished = -12;
}
