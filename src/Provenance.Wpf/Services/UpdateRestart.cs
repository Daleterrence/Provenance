namespace Provenance.Wpf.Services;

public sealed class UpdateRestart(IDialogs dialogs, Func<Task<bool>> prepare, Action exit, Func<string?>? blocked = null)
{
    public const string Title = "Restart Provenance";

    public const string Question =
        "Are you sure you want to restart Provenance to update now? This will close any instances of Provenance or FINAL FANTASY XI currently open!";

    private bool _busy;

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

    public void Cancelled() => Busy = false;
}
