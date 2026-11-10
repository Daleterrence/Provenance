namespace Provenance.Common.Otp;

public enum ToolView { Hidden, Missing, CantCheck, TooOld, Installing, Ready, DownloadFailed, CheckFailed, DownloadCantCheck, DownloadTooOld, NotSupported }

public static class ToolFix
{
    public static string? Label(OtpFix fix) => fix switch
    {
        OtpFix.Install => "INSTALL IT",
        OtpFix.OpenOnePasswordSettings => "OPEN 1PASSWORD SETTINGS",
        OtpFix.TypeInstead => "TYPE THE CODE INSTEAD",
        OtpFix.TryAgain => "TRY AGAIN",
        _ => null,
    };

    public static string Text(ToolView view, string tool) => view switch
    {
        ToolView.Missing => ToolTexts.Offer(FindState.NotInstalled, tool),
        ToolView.CantCheck => ToolTexts.Offer(FindState.CantCheck, tool),
        ToolView.TooOld => ToolTexts.Offer(FindState.TooOld, tool),
        ToolView.Installing => ToolTexts.Downloading(tool),
        ToolView.Ready => ToolTexts.Ready(tool),
        ToolView.DownloadFailed => ToolTexts.Result(tool, ToolInstall.DownloadFailed),
        ToolView.CheckFailed => ToolTexts.Result(tool, ToolInstall.CheckFailed),
        ToolView.DownloadCantCheck => ToolTexts.Result(tool, ToolInstall.CantCheck),
        ToolView.DownloadTooOld => ToolTexts.Result(tool, ToolInstall.TooOld),
        ToolView.NotSupported => ToolTexts.Result(tool, ToolInstall.NotSupported),
        _ => "",
    };

    public static OtpFix? Button(ToolView view, string tool) => view switch
    {
        ToolView.Missing or ToolView.CantCheck or ToolView.TooOld => OtpFix.Install,
        ToolView.DownloadFailed or ToolView.CheckFailed or ToolView.DownloadCantCheck or ToolView.DownloadTooOld => OtpFix.TryAgain,
        ToolView.Ready when tool == ToolFacts.OnePassword => OtpFix.OpenOnePasswordSettings,
        _ => null,
    };

    public static ToolView ViewFor(FindState state) => state switch
    {
        FindState.CantCheck => ToolView.CantCheck,
        FindState.TooOld => ToolView.TooOld,
        _ => ToolView.Missing,
    };

    public static ToolView ViewFor(ToolInstall failed) => failed switch
    {
        ToolInstall.CheckFailed => ToolView.CheckFailed,
        ToolInstall.CantCheck => ToolView.DownloadCantCheck,
        ToolInstall.TooOld => ToolView.DownloadTooOld,
        ToolInstall.NotSupported => ToolView.NotSupported,
        _ => ToolView.DownloadFailed,
    };

    // null once it's open, otherwise the line to show
    public static string? OpenSettings() => OnePassword.OpenSettings() ? null : OnePassword.OpenSettingsFailedText;
}
