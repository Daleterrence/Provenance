namespace Provenance.Common;

public static class AppPaths
{
    // null with no AppData at all, which would otherwise make a relative path, so callers just skip keeping the file
    public static string? UserData =>
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) is { Length: > 0 } root
            ? Path.Combine(root, "Provenance") : null;

    public const string LogsFolder = "logs";

    public static string Logs => Path.Combine(ProgramData, LogsFolder);

    public static string ProgramData =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Provenance");

    public static string Windows => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    public static string SystemX86 => Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
    public static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    public static string ProgramFilesX86 => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    // the bare system ProgramData folder (ProgramData is Provenance's own inside it)
    public static string CommonAppData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    public static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public static string Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public static string ExplorerExe => Path.Combine(Windows, "explorer.exe");

    public static string Special(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);
}
