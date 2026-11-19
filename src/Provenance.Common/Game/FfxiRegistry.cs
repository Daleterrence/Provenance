using Microsoft.Win32;
using Provenance.Common.PolFormat;

namespace Provenance.Common.Game;

public static class FfxiRegistry
{
    public static RegistryKey? Open(PolRegion region, bool write)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        return write ? hklm.CreateSubKey(PolPaths.FfxiKey(region), writable: true) : hklm.OpenSubKey(PolPaths.FfxiKey(region));
    }

    private static RegistryKey OpenForWrite(PolRegion region) =>
        Open(region, true) ?? throw Shown.Mark(new InvalidOperationException("Provenance couldn't open the FINAL FANTASY XI settings."));

    public static FfxiSettings LoadFrom(RegistryKey k)
    {
        var d = FfxiSettings.Defaults();
        var s = new FfxiSettings();
        foreach (var v in FfxiValues.All)
            switch (v)
            {
                case IntValue i:
                    int n = k.GetValue(i.Name) is int stored ? stored : i.Get(d);
                    i.Set(s, i.ReadsInRange ? i.Range(n) : n);
                    break;
                case TextValue t:
                    t.Set(s, k.GetValue(t.Name) as string ?? "");
                    break;
                case PadValue p when k.GetValue(p.Name) is string pm:
                    var parts = pm.Split(',');
                    for (int j = 0; j < 6 && j < parts.Length; j++)
                        s.PadMode[j] = int.TryParse(parts[j].Trim(), out var x) ? x : d.PadMode[j];
                    break;
                case PadValue:
                    s.PadMode = d.PadMode;
                    break;
            }
        return s;
    }

    public static bool NeedsFirstStart(PolRegion region)
    {
        using var k = Open(region, false);
        return k == null || new[] { "0001", "0002", "0003", "0004", "0034", "0037", "0038" }.Any(n => k.GetValue(n) is not int);
    }

    public static int WriteFirstStart(PolRegion region)
    {
        var (w, h) = Display.Primary();
        using var k = OpenForWrite(region);
        return FillMissing(k, FfxiSettings.FirstStart(w, h));
    }

    public static int FillMissing(RegistryKey k, FfxiSettings s)
    {
        int n = 0;
        if (k.GetValue("0001") is null && k.GetValue("0034") is int mode && mode != s.WindowMode)
        {
            k.SetValue("0034", s.WindowMode, RegistryValueKind.DWord);
            n++;
        }
        bool Missing(string name) => k.GetValue(name) is null;
        void Set(string name, object v, RegistryValueKind kind = RegistryValueKind.DWord)
        {
            k.SetValue(name, v, kind);
            n++;
        }
        foreach (var v in FfxiValues.All)
            switch (v)
            {
                case IntValue second when FfxiValues.IsSecond(second):
                    break;
                case IntValue i:
                    var partner = FfxiValues.Partner(i);
                    if (partner is null)
                    {
                        if (Missing(i.Name)) Set(i.Name, i.Get(s));
                    }
                    else if (Missing(i.Name) || Missing(partner.Name))
                    {
                        Set(i.Name, i.Get(s));
                        Set(partner.Name, partner.Get(s));
                    }
                    break;
                case FixedValue f when Missing(f.Name):
                    Set(f.Name, f.Value);
                    break;
                case TextValue t when Missing(t.Name):
                    Set(t.Name, t.Get(s), RegistryValueKind.String);
                    break;
                case PadValue p when Missing(p.Name):
                    Set(p.Name, string.Join(",", s.PadMode), RegistryValueKind.String);
                    break;
            }
        return n;
    }

    public static int SaveTo(RegistryKey k, FfxiSettings s, FfxiSettings original)
    {
        int n = 0;
        void Set(string name, object v, RegistryValueKind kind = RegistryValueKind.DWord)
        {
            k.SetValue(name, v, kind);
            n++;
        }
        foreach (var v in FfxiValues.All)
            switch (v)
            {
                case IntValue second when FfxiValues.IsSecond(second):
                    break;
                case BrightnessValue b:
                    var bright = MathF.Round(Math.Clamp(s.Brightness, -1f, 1f), 1);
                    if (bright != MathF.Round(Math.Clamp(original.Brightness, -1f, 1f), 1)) Set(b.Name, BitConverter.SingleToInt32Bits(bright));
                    break;
                case IntValue i:
                    var partner = FfxiValues.Partner(i);
                    if (partner is null)
                    {
                        int now = i.Range(i.Get(s)), was = i.Get(original);
                        if (now != (i.KeepsStored ? i.Range(was) : was)) Set(i.Name, now);
                    }
                    else if (i.Get(s) != i.Get(original) || partner.Get(s) != partner.Get(original))
                    {
                        Set(i.Name, i.Get(s));
                        Set(partner.Name, partner.Get(s));
                    }
                    break;
                case TextValue t when t.Get(s) != t.Get(original):
                    Set(t.Name, t.Get(s), RegistryValueKind.String);
                    break;
                case PadValue p when !s.PadMode.SequenceEqual(original.PadMode):
                    Set(p.Name, string.Join(",", s.PadMode), RegistryValueKind.String);
                    break;
            }
        return n;
    }
}
