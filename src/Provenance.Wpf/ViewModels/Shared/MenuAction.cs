namespace Provenance.Wpf.ViewModels.Shared;

public sealed record MenuAction(string Header, bool Enabled, Action? Run = null)
{
    public static MenuAction Separator { get; } = new("", true) { IsSeparator = true };

    public bool IsSeparator { get; init; }
    public string? Tip { get; init; }
    public string? Detail { get; init; }
    public bool? Checked { get; init; }
    public IReadOnlyList<MenuAction>? Items { get; init; }
}
