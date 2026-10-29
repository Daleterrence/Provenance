namespace Provenance.Common.Social;

// the viewer's own account checks (app.dll LM table at 0x104a6d68, US block, Content ID check at 0x10199093), so the
// game gets refused in the same cases with the same text
public static class PolAccountNotices
{
    // app string 0x65d9. A cancelled Content ID is just missing, so this covers never subscribed and cancelled
    public const string NoFfxiContentId =
        "You have no Content ID for FINAL FANTASY XI. Please obtain a new Content ID or reactivate " +
        "a cancelled one in order to play the game and/or to use the PlayOnline services.";

    // the Content ID list didn't load (twice), so fail closed like the viewer does on a bad list
    public const string ServiceUnknown =
        "PlayOnline didn't send your Content ID list, so Provenance couldn't check your " +
        "FINAL FANTASY XI service and the game wasn't started. Press Play to ask again.";

    // Blocks when the viewer's frame only offers Log Out or a fix-it page
    public sealed record Notice(int Status, string Code, bool Blocks, string Text);

    private static readonly Dictionary<int, Notice> Table = new[]
    {
        N(221, true, "Unable to log in due to unexpected error."),
        N(222, false, "Because the free trial period has ended, all relevant Content IDs have been cancelled automatically. If you complete procedures to transfer your account to regular billing-based gameplay within the allowed number of days (including today), you will be able to continue playing with your current character data."),
        N(223, false, "Due to internet congestion, it will take longer than usual to complete registration for PlayOnline. Please wait for an e-mail confirming its completion."),
        N(224, false, "Your account will be closed at the end of this month due to missing payment information. Please log in from a Japanese copy of the PlayOnline Viewer for further details."),
        N(225, false, "Your current payment method cannot be used at this time. Please log in from a Japanese Viewer which you originally registered with for further details."),
        N(226, true, "Your account has been temporarily closed due to problems with your payment information or with your past payment. Please login from a Japanese Viewer which you originally registered with for further details."),
        N(227, true, "Unable to log in due to an unexpected error."),
        N(228, true, "PlayOnline/FINAL FANTASY XI has been cancelled. Reactivate the PlayOnline/FINAL FANTASY XI service account to access services again."),
        N(229, true, "Your account has been temporarily closed due to problems with your payment information or with your past payment. Please log in from a Japanese copy of the PlayOnline Viewer for further details."),
        N(230, true, "Your account has been temporarily closed due to a problem with one of the member information verification procedures. Please log in from a Japanese copy of the PlayOnline Viewer for further details."),
        N(231, true, "This account has been suspended due to violation of the PlayOnline or FINAL FANTASY XI agreements. Please refer to the \"Account Penalties\" page under \"Online Manners\" in the \"Rules & Policies\" section of the official website for details on account penalties."),
        N(232, false, "There are problems with your payment information or your past payments. Please login from a Japanese Viewer which you originally registered with for further details."),
        N(233, true, "Your account has been temporarily closed due to problems with the member information verification procedure. Please login from a Japanese Viewer which you originally registered with for further details."),
        N(234, false, "Your Webmoney prepaid period is expiring for one or more services. Please log in from a Japanese copy of the PlayOnline Viewer and resolve this issue by the end of the month."),
        N(235, true, "The Beta test period has ended. The PlayOnline ID you used during the Beta test is no longer valid. To continue using PlayOnline, please re-register for membership and obtain a new PlayOnline ID."),
        N(236, false, "This month's fees have been billed."),
        N(237, true, "This account has been banned due to a violation of a service agreement, and can no longer be used. Refer to the \"Account Penalties\" page under \"Online Manners\" in the \"Rules and Policies\" section of the official website for details."),
        N(238, false, "The credit card you have registered expires this month. To continue using PlayOnline, please re-register a valid card by going to \"Update Payment Information\" in the \"Membership\" section of \"Service & Support.\""),
        N(239, true, "Unable to reactivate your PlayOnline account per your request. Please contact the Square Enix Support Center for further details if you have any questions."),
        N(240, true, "Your account is closed due to an expired card and unpaid fees. You can reactivate your account by paying these fees with a valid card."),
        N(241, true, "Your account has been closed due to unpaid fees resulting from declined authorization on your credit card. You can reactivate your account by paying these fees with a valid card."),
        N(242, true, "Your account is closed due to receiving a chargeback on past fees. You need to pay these fees before you are allowed to reactivate your account."),
        N(243, true, "Your account is closed due to receiving a chargeback on a payment for a previous charge. Please pay the outstanding amount."),
        N(244, true, "A Square Enix account is required to use PlayOnline services. Please obtain a Square Enix account before continuing."),
        N(245, true, "To use PlayOnline services, you will need to log in to the Square Enix Account Management System and reactivate them."),
        N(246, true, "A Square Enix account is required to reactivate PlayOnline services. Please obtain a Square Enix account before continuing."),
        N(247, true, "Due to an unresolved issue with service fees, PlayOnline services have been suspended. To use these services again, please pay the overdue service fees before continuing."),
        N(248, true, "Due to an unresolved issue with service fees, PlayOnline services have been suspended. To use these services again, please pay the overdue service fees."),
        N(249, false, "There are Content IDs with billing periods that will soon end. If you do not perform a Content ID contract extension by the end of the month (or by the date that the period ends), the associated Content IDs will be automatically cancelled and made unusable the following day. If you would like to continue to use PlayOnline services, please obtain a Square Enix account and perform the necessary procedures."),
        N(250, false, "The credit card registered for your payment method will expire at the end of the month. If the credit card's registered details (the expiration date, or a new card) are not updated, the associated Content IDs will be automatically cancelled and made unusable. If you would like to continue to use PlayOnline services, please obtain a Square Enix account and perform the necessary procedures."),
        N(251, true, "Due to either an unresolved issue with service fees or a violation of the PlayOnline or FINAL FANTASY XI agreements, PlayOnline services have been suspended. Please contact the Square Enix Support Center for details."),
        N(252, false, "The credit card registered for your payment method will expire at the end of the month. Please update the credit card information before it expires. You can do so from \"Register/Update Payment Method\" within the Square Enix Account Management System."),
    }.ToDictionary(n => n.Status);

    private static Notice N(int status, bool blocks, string text) =>
        new(status, $"LM-{status - 220:00}", blocks, text);

    public static string? PlayBlockedReason(PolSocialSnapshot snap) => snap.FfxiService switch
    {
        "none" => NoFfxiContentId,
        "unknown" => ServiceUnknown,
        _ => For(snap.Notice) is { Blocks: true } notice ? $"{notice.Text} ({notice.Code})" : null,
    };

    // an unknown service isn't a refusal, Play stays on and asks POL again
    public static bool CanPlay(string? reason) => reason is null || reason == ServiceUnknown;

    // status byte from svc 0xd20. 253..255 have no row, the viewer shows LM-01 for them
    public static Notice? For(int status) =>
        Table.TryGetValue(status, out var n) ? n : status is >= 253 and <= 255 ? Table[221] : null;

    public static string SignInRefused(int reason) => reason switch
    {
        -202 or -203 => "Incorrect PlayOnline ID and/or password.",
        -205 => "Your account login has been temporarily disabled due to three consecutive password errors. Please try again later.",
        -201 => "This PlayOnline ID is already logged in elsewhere.",
        -204 => "This version of PlayOnline is too old to log in with.",
        -5310 => "Login to your account has been temporarily restricted. Instructions for lifting this restriction have been sent to the email address registered to your Square Enix Account.",
        -5311 or -5312 => "The Square Enix ID, Square Enix password, or one-time password entered is incorrect.",
        -5426 => "Square Enix Account Certification Error.",
        -0x40c => "Connection cancelled.",
        <= -221 and >= -252 => For(-reason) is { } n ? $"{n.Text} ({n.Code})" : "Unable to log in due to unexpected error.",
        _ => $"Network Connection Error (code {reason}).",
    };

    // the server let it in but the account gates refused, so the session got signed out before anything started
    public static string Blocked(string what)
    {
        if (what.StartsWith("LM-", StringComparison.Ordinal) && int.TryParse(what[3..], out var lm) &&
            For(220 + lm) is { } n)
            return $"{n.Text} ({n.Code})";
        if (what == "disconnect-bit") return "PlayOnline disconnected this account as soon as it logged in.";
        if (what == "jail")
            return "This PlayOnline account is restricted. See http://info.playonline.com/snews/us/jail.pml for details.";
        if (what.StartsWith("load:", StringComparison.Ordinal) && int.TryParse(what[(what.IndexOf('=') + 1)..], out var code))
            return code switch
            {
                -5434 => "Service for the specified PlayOnline ID has been suspended.",
                -5333 => "Logins from this PlayOnline ID are restricted to a specific IP address.",
                -5332 => "Your PlayOnline password has been changed. Log in again with the new password.",
                -5441 or -5442 => "Unauthorized connection to server detected.",
                >= -5433 and <= -5430 => "Connection terminated or not available.",
                -5370 or -5371 or (>= -5438 and <= -5435) => "The server may be experiencing problems.",
                _ => $"PlayOnline turned this account away (code {code}).",
            };
        return "PlayOnline turned this account away.";
    }

    public static string SessionLost(int reason) => reason switch
    {
        -0x40f => "Disconnected. The current PlayOnline ID was used to log in from another terminal.",
        -0x400 or -0x401 or -0x40e => "Disconnected. Now offline.",
        -251 => "Disconnected from PlayOnline.",
        -207 => "Connection terminated or not available.",
        <= -221 and >= -252 => For(-reason) is { } n ? $"{n.Text} ({n.Code})" : "Disconnected from PlayOnline.",
        _ => $"Disconnected from PlayOnline (code {reason}).",
    };

    // viewer message 0x7007
    public const string BadPassword =
        "Your PlayOnline password is no longer valid. Change your PlayOnline password on the PlayOnline website.";
}
