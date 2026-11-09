namespace Provenance.Common.Otp;

// the lines about a command-line tool. A fetch that can't go ahead says Problem, the dialogs say Offer, which adds that
// Provenance can download a copy
public static class ToolTexts
{
    public static string Problem(FindState state, string tool) => state switch
    {
        FindState.CantCheck => $"Provenance couldn't check the {ToolFacts.Name(tool)} command-line tool.",
        FindState.TooOld => $"The {ToolFacts.Name(tool)} command-line tool is too old for Provenance.",
        _ => $"The {ToolFacts.Name(tool)} command-line tool isn't installed.",
    };

    public static string Offer(FindState state, string tool)
    {
        var name = ToolFacts.Name(tool);
        return state switch
        {
            FindState.CantCheck => $"{Problem(state, tool)} It can download its own copy from {name}.",
            FindState.TooOld => $"{Problem(state, tool)} It can download a newer copy from {name}.",
            _ => $"Provenance needs the {name} command-line tool. It can download its own copy from {name}.",
        };
    }

    public static string Downloading(string tool) => $"Downloading the {ToolFacts.Name(tool)} command-line tool...";

    public static string Ready(string tool) => $"The {ToolFacts.Name(tool)} command-line tool is ready.";

    public static string Result(string tool, ToolInstall result)
    {
        var name = ToolFacts.Name(tool);
        return result switch
        {
            ToolInstall.DownloadFailed => $"Couldn't download the {name} command-line tool.",
            ToolInstall.CheckFailed => $"The download didn't check out as {name}'s own, so Provenance deleted it.",
            ToolInstall.CantCheck => $"Provenance couldn't check the download from {name}, so it deleted it.",
            ToolInstall.TooOld => $"The download from {name} was an older version, so Provenance deleted it.",
            ToolInstall.NotSupported => $"{name} doesn't make its command-line tool for this version of Windows.",
            _ => Ready(tool),
        };
    }
}
