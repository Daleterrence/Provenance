namespace Provenance.Common.Game.ThirdParty;

public static class LuaAddonFolders
{
    // addons\<name>\<name>.lua, the name matching its folder in any case (libs is shared code, never an addon). A
    // folder that can't be read (a dangling junction, no access) is just skipped
    public static IReadOnlyList<(string Name, string Folder, string Main)> Walk(string addonsDir, bool skipDotted)
    {
        List<string> folders;
        try { folders = Directory.Exists(addonsDir) ? Directory.EnumerateDirectories(addonsDir).ToList() : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
        var found = new List<(string, string, string)>();
        foreach (var folder in folders)
        {
            var name = Path.GetFileName(folder);
            if (name.Length == 0 || name.Equals("libs", StringComparison.OrdinalIgnoreCase) || (skipDotted && name[0] == '.')) continue;
            string? main;
            try
            {
                main = Directory.EnumerateFiles(folder, "*.lua")
                    .FirstOrDefault(f => Path.GetFileName(f).Equals(name + ".lua", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            if (main is not null) found.Add((name, folder, main));
        }
        return found;
    }
}
