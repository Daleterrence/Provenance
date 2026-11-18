namespace Provenance.Wpf.Services;

// Restart Now on the update banner: asks first, then the update's checked and downloaded and everything else is closed
// (prepare, which closes nothing if it can't), then this copy exits. Its session is logged out and any game ended in
// App.OnExit, and Update.exe does the install. blocked says why it can't start right now, if it can't
public sealed class UpdateRestart(IDialogs dialogs, Func<Task<bool>> prepare, Action exit, Func<string?>? blocked = null)
{
    public const string Title = "Restart Provenance";

    public const string Question =
        "Are you sure you want to restart Provenance to update now? This will close any instances of Provenance or FINAL FANTASY XI currently open!";

    private bool _busy;

    // while the update's under way, the window shouldn't start anything
    public bool Busy
    {
        get => _busy;
        private set
        {
            if (_busy == value) return;
            _busy = value;
            BusyChanged?.Invoke();
        }
    }

    public string? BlockedReason => blocked?.Invoke();

    public event Action? BusyChanged;

    public async Task AskAsync()
    {
        if (Busy) return;
        if (BlockedReason is { } why)
        {
            dialogs.Info(Title, why);
            return;
        }
        Busy = true;
        bool going = false;
        try
        {
            if (!dialogs.Confirm(Title, Question, "YES", ConfirmStyle.Destructive, "NO")) return;
            going = await prepare();
            if (going) exit();
        }
        finally
        {
            if (!going) Busy = false;
        }
    }

    // the exit was turned down after all
    public void Cancelled() => Busy = false;
}
