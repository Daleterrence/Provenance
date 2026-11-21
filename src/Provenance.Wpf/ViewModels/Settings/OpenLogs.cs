using System.IO;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common;
using Provenance.Wpf.Services;
using Provenance.Wpf.ViewModels.Shared;
using Serilog;

namespace Provenance.Wpf.ViewModels.Settings;

public sealed partial class OpenLogs
{
    public string Folder => FolderForTest();

    public StatusLine Status { get; } = new();

    internal Func<string> FolderForTest { get; set; } = () => AppPaths.Logs;

    internal Action<string> ShowFolderForTest { get; set; } = Explorer.ShowFolder;

    [RelayCommand]
    private void Open()
    {
        var folder = Folder;
        if (!Directory.Exists(folder))
        {
            Status.Fail($"There's no logs folder at {folder}.");
            return;
        }
        try
        {
            ShowFolderForTest(folder);
            Status.Clear();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open {Folder}", folder);
            Status.Fail("Windows couldn't open the logs folder.");
        }
    }
}
