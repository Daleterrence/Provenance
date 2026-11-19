using System.Globalization;

namespace Provenance.Common.PolFormat;

// one entry line of the report: <same|moved|notfound> <module> <old> <new> <required|optional> <name> [| why]...
public sealed record PolCheckEntry(string Status, string Module, int Old, int? New, bool Required, string Name, string[] Notes);

public sealed record PolCheckReport(
    IReadOnlyDictionary<string, string> Modules, IReadOnlyList<PolCheckEntry> Entries, IReadOnlyDictionary<string, int[]> Summaries,
    string Result, int RequiredNotFound)
{
    public static PolCheckReport Parse(string text)
    {
        var modules = new Dictionary<string, string>();
        var entries = new List<PolCheckEntry>();
        var summaries = new Dictionary<string, int[]>();
        var result = "";
        var missing = -1;
        var header = false;
        int Offset(string s) => int.Parse(s.TrimStart('+'), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.TrimEnd('\r').Split(' ', 6);
            switch (f[0])
            {
                case "pol-check": header = f.Length == 2 && f[1] == "1"; break;
                case "module": modules[f[1]] = f[2]; break;
                case "same" or "moved" or "notfound":
                    if (f.Length < 6) throw new FormatException($"a short entry line: {line}");
                    var notes = f[5].Split(" | ");
                    entries.Add(new(f[0], f[1], Offset(f[2]), f[3] == "-" ? null : Offset(f[3]), f[4] == "required", notes[0], notes[1..]));
                    break;
                case "summary": summaries[f[1]] = [.. f.Skip(2).Select(s => int.Parse(s, CultureInfo.InvariantCulture))]; break;
                case "result": result = f[1]; missing = int.Parse(f[2], CultureInfo.InvariantCulture); break;
            }
        }
        if (!header || result == "") throw new FormatException("the report has no 'pol-check 1' line or no result line, so it was cut short");
        return new(modules, entries, summaries, result, missing);
    }
}
