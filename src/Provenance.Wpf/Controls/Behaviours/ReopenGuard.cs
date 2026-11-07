namespace Provenance.Wpf.Controls.Behaviours;

// a click on the button of an open menu or popup closes it, and this stops that same click opening it again. Dumb, but
// it works
public sealed class ReopenGuard
{
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(300);
    private DateTime _closedAt;

    public void MarkClosed() => _closedAt = DateTime.UtcNow;

    public void OnClosed(object? sender, EventArgs e) => MarkClosed();

    public bool JustClosed => DateTime.UtcNow - _closedAt < Window;
}
