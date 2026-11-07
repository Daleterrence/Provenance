namespace Provenance.Common.Otp;

public enum ToolView { Hidden, Missing, CantCheck, TooOld, Installing, Ready, DownloadFailed, CheckFailed, DownloadCantCheck, DownloadTooOld, NotSupported }

// the line and the button for a command-line tool that's missing or broken, so Edit account and the code box say the same
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
        ToolView.Missing => CliTools.MissingText(tool),
        ToolView.CantCheck => CliTools.CantCheckText(tool),
        ToolView.TooOld => CliTools.TooOldText(tool),
        ToolView.Installing => CliTools.DownloadingText(tool),
        ToolView.Ready => CliTools.ReadyText(tool),
        ToolView.DownloadFailed => CliTools.ResultText(tool, ToolInstall.DownloadFailed),
        ToolView.CheckFailed => CliTools.ResultText(tool, ToolInstall.CheckFailed),
        ToolView.DownloadCantCheck => CliTools.ResultText(tool, ToolInstall.CantCheck),
        ToolView.DownloadTooOld => CliTools.ResultText(tool, ToolInstall.TooOld),
        ToolView.NotSupported => CliTools.ResultText(tool, ToolInstall.NotSupported),
        _ => "",
    };

    public static OtpFix? Button(ToolView view, string tool) => view switch
    {
        ToolView.Missing or ToolView.CantCheck or ToolView.TooOld => OtpFix.Install,
        ToolView.DownloadFailed or ToolView.CheckFailed or ToolView.DownloadCantCheck or ToolView.DownloadTooOld => OtpFix.TryAgain,
        ToolView.Ready when tool == CliTools.OnePassword => OtpFix.OpenOnePasswordSettings,
        _ => null,
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
    public static string? OpenSettings() => OtpSources.OpenOnePasswordSettings() ? null : OtpSources.OpenSettingsFailedText;
}
