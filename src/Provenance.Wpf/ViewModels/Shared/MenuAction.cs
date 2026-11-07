namespace Provenance.Wpf.ViewModels.Shared;

public sealed record MenuConfirm(string Title, string Body, string Ok);

// one line of a right-click or dots menu, who can do what already worked out. Items makes it a submenu
public sealed record MenuAction(string Header, bool Enabled, Action? Run = null)
{
    public static MenuAction Separator { get; } = new("", true) { IsSeparator = true };

    public bool IsSeparator { get; init; }
    public string? Tip { get; init; }
    public string? Detail { get; init; }
    public bool? Checked { get; init; }
    public IReadOnlyList<MenuAction>? Items { get; init; }

    // asks first, and only goes on if the answer's yes
    public static Action Asking(Func<MenuConfirm, bool> confirm, MenuConfirm question, Action then) => () =>
    {
        if (confirm(question)) then();
    };
}
