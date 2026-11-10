using Provenance.Common.Security;
using Provenance.Common.Settings;
using Serilog;
using Provenance.Common.PolFormat;
using Provenance.Common.IO;

namespace Provenance.Common.Game;

public static class PolOptions
{
    public static readonly int[] AutoDisconnectChoices = [-1, 10, 20, 30, 40, 50, 60];

    public const string LinkedText = "Provenance can't change these settings because your PlayOnline Viewer folder is redirected to another location.";

    private static readonly string[] MessageFolders = [@"s\a", @"s\b", @"r\a", @"r\b"];

    // nothing chosen yet shows what FFXI does on an account with no option.bin: the filter on, auto-disconnect off
    public static bool ChatFilter(LauncherSettings s) => s.ChatLanguageFilter ?? true;
    public static int AutoDisconnect(LauncherSettings s) => s.AutoDisconnectMinutes ?? -1;

    // there's no telling POL's defaults from someone's own choice, so an existing install's accounts are left alone
    // till one's picked in Settings. Mine start with both off
    public static void NewInstall(LauncherSettings s)
    {
        s.ChatLanguageFilter ??= false;
        s.AutoDisconnectMinutes ??= -1;
    }

    // null for a file that's gone or isn't a readable option.bin. Throws when it's there but can't be read just now
    private static byte[]? ReadPlain(string path)
    {
        try
        {
            using var fs = SharedFile.OpenRead(path);
            if (fs.Length > PolOptionFile.MaxSize) return null;
            var bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
            return PolOptionFile.Decrypt(bytes);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException) { return null; }
    }

    internal static string Template(string viewerDir, PolRegion region)
    {
        var eu = Path.Combine(viewerDir, "EU", "EN", "option.bin");
        return region == PolRegion.Europe && File.Exists(eu) ? eu : Path.Combine(viewerDir, "default", "pub", "home", "option.bin");
    }

    // the hook won't use a slot whose msg folders go through a link and stays in home84, so that's the one FFXI reads
    public static int SlotInUse(string viewerDir, int slot)
    {
        if (!HomeSlots.Valid(slot)) return HomeSlots.Shared;
        var msg = Path.Combine(viewerDir, "pub", $"home{slot:00}", "msg");
        return MessageFolders.All(f => FolderPins.Plain(viewerDir, Path.Combine(msg, f), lastIsFolder: true)) ? slot : HomeSlots.Shared;
    }

    // before every game start. A slot with no option.bin (or one polcore can't read) gets the viewer's template first,
    // the same as the viewer does for a new member
    public static void Apply(LauncherSettings s, string viewerDir, int slot, PolRegion region)
    {
        if (s.ChatLanguageFilter is null && s.AutoDisconnectMinutes is null) return;
        // the viewer folder may be one users can write to, so nothing below it can be a link either, it'd be an elevated
        // write through one
        if (FolderPins.Linked(viewerDir))
        {
            Log.Warning("The viewer folder {Viewer} is reached through a link, POL settings not written for home{Slot:00}", viewerDir, slot);
            return;
        }
        var dir = Path.Combine(viewerDir, "pub", $"home{slot:00}");
        var path = Path.Combine(dir, "option.bin");
        if (!FolderPins.Plain(viewerDir, path, lastIsFolder: false))
        {
            Log.Warning("pub\\home{Slot:00} goes through a link, its POL settings are left alone", slot);
            return;
        }
        bool fresh = false;
        var plain = File.Exists(path) ? ReadPlain(path) : null;
        if (plain is null)
        {
            plain = ReadPlain(Template(viewerDir, region));
            fresh = true;
            if (plain is null)
            {
                Log.Warning("No readable option.bin template in {Viewer}, POL settings not written for home{Slot:00}", viewerDir, slot);
                return;
            }
        }
        int filter = ChatFilter(s) ? 0 : 1, minutes = AutoDisconnect(s);
        if (!fresh && PolOptionFile.Read(plain, PolOptionFile.ChatFilter) == filter &&
            PolOptionFile.Read(plain, PolOptionFile.AutoDisconnect) == minutes) return;
        if (!PolOptionFile.Write(plain, PolOptionFile.ChatFilter, filter) ||
            !PolOptionFile.Write(plain, PolOptionFile.AutoDisconnect, minutes))
        {
            Log.Warning("option.bin for home{Slot:00} has no usable chat filter or auto-disconnect entry", slot);
            return;
        }
        using var pins = FolderPins.HoldExact(viewerDir, dir);
        if (pins is null)
        {
            Log.Warning("pub\\home{Slot:00} couldn't be held for the write, its POL settings are left alone", slot);
            return;
        }
        SafeWrite.Replace(pins, dir, path, fs => fs.Write(PolOptionFile.Encrypt(plain)));
        Log.Information("POL settings for home{Slot:00}{New}: chat language filter {Filter}, auto-disconnect {Minutes}",
                        slot, fresh ? " (from the template)" : "", filter == 0, minutes);
    }
}
