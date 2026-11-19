using Provenance.Common.PolFormat;

namespace Provenance.Common.Social;

public sealed class ComposeRules
{
    public bool SingleLine { get; init; }
    public int MaxChars { get; init; } = int.MaxValue;
    public Func<int>? MaxBytes { get; init; }
    public Func<string, string>? Filter { get; init; }

    public static string Flatten(string text) => text.Replace("\r", "").Replace('\n', ' ');

    public string Fit(string text, string add)
    {
        if (add.Length > MaxChars - text.Length)
        {
            int room = Math.Max(0, MaxChars - text.Length);
            if (room > 0 && room < add.Length && char.IsHighSurrogate(add[room - 1])) room--;
            add = add[..room];
        }
        if (MaxBytes is { } bytes) add = ShiftJis.Fit(add, Math.Max(0, bytes() - ShiftJis.ByteCount(text)));
        return add;
    }

    public string Filtered(string add) => Filter is null ? add : Filter(add);
}
