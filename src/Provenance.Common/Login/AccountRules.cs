using Provenance.Common.Credentials;
using Provenance.Common.Otp;
using Provenance.Common.Social;

namespace Provenance.Common.Login;

public enum AccountField { PolId, PolPassword, SquareEnixId, SquareEnixPassword, OtpItem }

public sealed record FormProblem(string Text, AccountField Field);

public static class AccountRules
{
    public enum SideEdit
    {
        Keep,      // ID and password both empty, the saved entry's neither read nor written
        Replace,
        Fill,
    }

    public static SideEdit SideEditOf(string id, string password) =>
        id.Length == 0 && password.Length == 0 ? SideEdit.Keep
        : id.Length > 0 && password.Length > 0 ? SideEdit.Replace
        : SideEdit.Fill;

    // a key that's gone still passes the header test, so the run's details-again text is what tells
    public static bool NeedsAllDetails(bool usable, string? multiProblem) =>
        !usable || multiProblem == HookExitText.DetailsAgainText;

    public static bool FillFailed(SideEdit polEdit, PolAccount? pol, SideEdit squareEnixEdit, SquareEnixAccount? squareEnix) =>
        (polEdit == SideEdit.Fill && pol is null) || (squareEnixEdit == SideEdit.Fill && squareEnix is null);

    public static bool ChangesLogIn(AccountDialogResult r, (string? PolId, string? SeaId) saved,
                                       bool useOtp, string otpSource, string otpItem, string otpAccount = "") =>
        (r.PolId.Length > 0 && !string.Equals(r.PolId, saved.PolId, StringComparison.OrdinalIgnoreCase))
        || (r.SeaId.Length > 0 && !string.Equals(r.SeaId, saved.SeaId, StringComparison.OrdinalIgnoreCase))
        || r.PolPassword.Length > 0 || r.SeaPassword.Length > 0
        || r.UseOtp != useOtp || r.OtpSource != otpSource || r.OtpItem != otpItem || r.OtpAccount != otpAccount;

    public static AccountDialogResult WithoutPasswords(AccountDialogResult typed) =>
        typed with { PolPassword = "", SeaPassword = "" };

    // Edit account's checks, in the order the form shows them. IDs come in trimmed (POL's in capitals), the item as
    // typed, and required means adding or details that can't be used any more
    public static FormProblem? CheckForm(bool required, string polId, string polPassword, string squareEnixId, string squareEnixPassword,
                                         bool useOtp, string otpSource, string otpItem)
    {
        if ((required || polId.Length > 0) && (polId.Length != 8 || !polId.All(char.IsAsciiLetterOrDigit)))
            return new("A PlayOnline ID is 8 letters and numbers.", AccountField.PolId);
        if (required && polPassword.Length == 0) return new("Enter your PlayOnline password.", AccountField.PolPassword);
        if (polPassword.Length > HookExitText.MaxPolPasswordLength)
            return new("A PlayOnline password can't be longer than 16 characters.", AccountField.PolPassword);
        if (PasswordProblem(polPassword) is { } polPasswordProblem) return new(polPasswordProblem, AccountField.PolPassword);
        if (required && squareEnixId.Length == 0) return new("Enter your Square Enix ID.", AccountField.SquareEnixId);
        if (squareEnixId.Contains('@')) return new("Use your Square Enix ID here, not your email address.", AccountField.SquareEnixId);
        if (SquareEnixIdProblem(squareEnixId) is { } squareEnixIdProblem) return new(squareEnixIdProblem, AccountField.SquareEnixId);
        if (required && squareEnixPassword.Length == 0) return new("Enter your Square Enix password.", AccountField.SquareEnixPassword);
        if (PasswordProblem(squareEnixPassword) is { } squareEnixPasswordProblem) return new(squareEnixPasswordProblem, AccountField.SquareEnixPassword);
        if (useOtp && otpSource.Length > 0 && otpItem.Trim().Length == 0)
            return new("Enter the item that holds the authenticator, or choose to type the code in.", AccountField.OtpItem);
        if (useOtp && ToolFacts.ForSource(otpSource) is not null && OtpSources.StartsWithDash(otpItem))
            return new(OtpSources.DashText, AccountField.OtpItem);
        return null;
    }

    private static string? ValidateDetails(string polId, string polPassword, string squareEnixId, string squareEnixPassword)
    {
        if (string.IsNullOrWhiteSpace(polId)) return "Enter your PlayOnline ID.";
        if (polId.Trim().Length != 8 || !polId.Trim().All(char.IsAsciiLetterOrDigit))
            return "A PlayOnline ID is 8 letters and numbers.";
        if (string.IsNullOrEmpty(polPassword)) return "Enter your PlayOnline password.";
        if (string.IsNullOrWhiteSpace(squareEnixId)) return "Enter your Square Enix ID.";
        if (squareEnixId.Contains('@')) return "Use your Square Enix ID here, not your email address.";
        if (string.IsNullOrEmpty(squareEnixPassword)) return "Enter your Square Enix password.";
        return null;
    }

    public sealed record LogInDetails(PolAccount? Pol, SquareEnixAccount? SquareEnix, string? Problem)
    {
        public bool Missing => Pol is null && Problem is null;
    }

    public static LogInDetails PrepareLogIn(ICredentialService credentials, string profileId)
    {
        var pol = credentials.LoadPolAccount(profileId);
        var squareEnix = credentials.LoadSquareEnixAccount(profileId);
        if (pol is null || squareEnix is null) return new(null, null, null);
        if (ValidateDetails(pol.PolId, pol.Password, squareEnix.Username, squareEnix.Password) is { } problem)
            return new(null, null, problem + " Press Edit account to fix it.");
        return new(new PolAccount(pol.PolId.Trim().ToUpperInvariant(), pol.Password), new SquareEnixAccount(squareEnix.Username.Trim(), squareEnix.Password), null);
    }

    // both IDs there means the entries exist but won't unwrap (a key that's gone, say)
    private static bool BothIds((string? PolId, string? SeaId) ids) => !string.IsNullOrEmpty(ids.PolId) && !string.IsNullOrEmpty(ids.SeaId);

    public static string MissingDetailsText((string? PolId, string? SeaId) ids) =>
        BothIds(ids) ? HookExitText.DetailsAgainText : "This account's login details are missing. Press Edit account to add them.";

    // both entries there but unusable is a Credential failure, so it stays off until edited (like hook exit 11)
    public static AccountFailure MissingDetailsFailure((string? PolId, string? SeaId) ids) =>
        new(BothIds(ids) ? AccountFailureKind.Credential : AccountFailureKind.Account, MissingDetailsText(ids));

    // the login only finishes once the hook's written a snapshot, so a missing one is the file being gone or unreadable
    // since (the session ending, say), not anything POL did
    public static AccountFailure? PlayGateFailure(PolSocialSnapshot? snap)
    {
        if (snap is null)
            return new(AccountFailureKind.Account, "Provenance couldn't read this account's details after it logged in, so the game wasn't started.");
        return PolAccountNotices.PlayBlockedReason(snap) is { } blocked ? new(AccountFailureKind.Account, blocked) : null;
    }

    // an unread or unanswered Play wants a restart so the run stops, a refusal or a lost session is this account's problem
    public static AccountFailureKind PlayFailureKind(string text) =>
        text == HookExitText.PlayUnreadableText ? AccountFailureKind.Install : AccountFailureKind.Account;

    internal static string TitleFor(int code) => code switch
    {
        LoginCodes.AuthFailed => "Login not accepted",
        LoginCodes.Unfinished => "Login didn't finish",
        LoginCodes.NoPolHandle => "PlayOnline handle needed",
        LoginCodes.HandleRejected => "Handle not accepted",
        LoginCodes.Stalled => "PlayOnline stopped responding",
        LoginCodes.AccountBlocked => "PlayOnline account notice",
        LoginCodes.LauncherFailed => "PlayOnline didn't start",
        LoginCodes.DetailsUnreadable => "Account details incomplete",
        _ => "Couldn't log in",
    };

    // never a retry by itself after a refusal, it'd stretch a three-strikes lockout. A refused password or a handle
    // problem is the user's to fix, anything else might need the log
    public static (string Title, string Text, bool AutoOff, bool OfferLogs) LoginFailure(int code, string? detail, bool autoLogin)
    {
        bool autoOff = code is LoginCodes.AuthFailed or LoginCodes.AccountBlocked
                           or LoginCodes.DetailsUnreadable && autoLogin;
        var why = detail ?? $"Provenance couldn't log in to PlayOnline (code {code}).";
        if (code == LoginCodes.AuthFailed)
            why += "\n\nIf your password has changed, press Edit account to update it, then log in again.";
        if (autoOff)
            why += "\n\nProvenance has turned off Log in automatically, so it won't try this account again by itself.";
        return (TitleFor(code), why, autoOff,
                code is LoginCodes.Stalled or LoginCodes.LauncherFailed or LoginCodes.Unfinished
                || TitleFor(code) == "Couldn't log in");
    }

    // the hook hands these to POL in the ANSI code page, so anything outside printable ASCII arrives changed, gets
    // refused, and the refusal counts toward the lockout
    public static string? PasswordProblem(string password) =>
        IsPrintableAscii(password) ? null : "Passwords can only use English letters, numbers and keyboard symbols.";

    public static string? SquareEnixIdProblem(string squareEnixId) =>
        IsPrintableAscii(squareEnixId) ? null : "A Square Enix ID can only use English letters, numbers and keyboard symbols.";

    private static bool IsPrintableAscii(string s) => s.All(c => c is >= '!' and <= '~');   // 0x21 to 0x7E
}
