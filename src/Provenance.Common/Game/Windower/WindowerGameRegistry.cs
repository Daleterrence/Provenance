using Microsoft.Win32;
using Provenance.Common.PolFormat;

namespace Provenance.Common.Game.Windower;

public sealed record WindowerRegistryValue(string Name, RegistryValueKind Kind, object Value);

// what Windower.exe writes into FFXI's settings before every launch. Everything else in the key is the shared FFXI
// settings and stays as it is
public static class WindowerGameRegistry
{
    private const string DefaultPadMode = "0,0,0,0,0,0";

    public static IReadOnlyList<WindowerRegistryValue> Values(WindowerProfile profile, int width, int height, object? padMode = null)
    {
        var s = WindowerSizes.For(profile.UIScale, width, height);
        var list = new List<WindowerRegistryValue>();
        foreach (var row in FfxiValues.All.Where(v => v.Windower))
            list.Add(row is PadValue
                ? new(row.Name, RegistryValueKind.String, PadMode(padMode as string, profile.AlwaysEnableGamepad))
                : new(row.Name, RegistryValueKind.DWord, FromProfile(row.Name, profile, s)));
        return list;
    }

    private static int FromProfile(string name, WindowerProfile profile, WindowerSizes s) => name switch
    {
        "0000" => profile.MipMapLevel,
        "0001" => s.Width,
        "0002" => s.Height,
        "0003" => Scaled(s.Width, profile.SuperSampling),
        "0004" => Scaled(s.Height, profile.SuperSampling),
        "0007" => profile.Sound ? 1 : 0,
        "0017" => profile.BumpMapping ? 1 : 0,
        "0021" => profile.HardwareMouse ? 1 : 0,
        "0028" => BitConverter.SingleToInt32Bits(profile.Gamma),
        "0034" => profile.WindowMode switch { WindowerWindowMode.Window => 1, WindowerWindowMode.Fullscreen => 0, _ => 3 },
        "0035" => profile.AlwaysPlaySound ? 1 : 0,
        "0037" => s.UIScale != 1f ? s.UIWidth : 0,
        "0038" => s.UIScale != 1f ? s.UIHeight : 0,
        _ => throw new InvalidOperationException($"Windower has no profile value for {name}."),
    };

    // Windower crashes on a padmode000 that isn't a string, here it just counts as the default
    public static string PadMode(string? current, bool alwaysEnableGamepad)
    {
        var fields = (current ?? DefaultPadMode).Split(',').ToList();
        while (fields.Count < 6) fields.Add("0");
        fields[4] = alwaysEnableGamepad ? "1" : "0";
        return string.Join(",", fields);
    }

    // a float times the size, then .NET's Convert.ToInt32 (ties go to even). Windower would crash on NaN or an
    // overflow, here they're 1x and the int range
    internal static int Scaled(int size, float superSampling)
    {
        float f = Math.Max(1f, size * superSampling);
        if (float.IsNaN(f)) f = Math.Max(1f, size);
        double r = Math.Round((double)f, MidpointRounding.ToEven);
        return r >= int.MaxValue ? int.MaxValue : (int)r;
    }

    // FinalFantasyXI, then FinalFantasyXITestClient if that's there too. Nothing at all when the main key's missing,
    // same as Windower
    public static IReadOnlyList<string> Apply(WindowerProfile profile, PolRegion region, int width, int height, RegistryKey? root = null)
    {
        var path = PolPaths.VendorKey(region);
        var written = new List<string>();
        var hklm = root is null ? RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32) : null;
        try
        {
            using var parent = (root ?? hklm!).OpenSubKey(path);
            if (parent is null) return written;
            using (var main = parent.OpenSubKey("FinalFantasyXI", writable: true))
            {
                if (main is null) return written;
                Write(main, profile, width, height);
                written.Add(path + @"\FinalFantasyXI");
            }
            using var test = parent.OpenSubKey("FinalFantasyXITestClient", writable: true);
            if (test is not null)
            {
                Write(test, profile, width, height);
                written.Add(path + @"\FinalFantasyXITestClient");
            }
            return written;
        }
        finally { hklm?.Dispose(); }
    }

    private static void Write(RegistryKey key, WindowerProfile profile, int width, int height)
    {
        foreach (var v in Values(profile, width, height, key.GetValue("padmode000")))
            key.SetValue(v.Name, v.Value, v.Kind);
    }
}
