using System.IO;
using Provenance.Common.Game.Ashita;
using Provenance.Common.Game.ThirdParty;
using Provenance.Common.Game.Windower;
using Provenance.Common.Security;
using Provenance.Common.Settings;
using Provenance.Wpf.Services;

namespace Provenance.Wpf.ViewModels.Settings;

internal readonly record struct FolderAnswer(string? Dir, bool Linked, bool Pending,
    IReadOnlyList<string>? Profiles = null, IReadOnlyList<(string File, string Shown)>? Boots = null, string? LastBoot = null);

// a share can take the SMB timeout to answer, so it's looked at off the UI thread and the page's told when it knows
internal sealed class HookFolderProbe
{
    private readonly Func<string, bool> _slow;
    private readonly Func<bool, string, FolderAnswer> _look;
    private readonly Action<Action> _background, _post;
    internal static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(3), AliveWait = TimeSpan.FromMilliseconds(250);

    // one for the main window, the Game page has its own
    internal static HookFolderProbe Shared { get; } = new(post: a =>
    {
        var ui = new UiThread();
        if (ui.Available) ui.Post(a);
        else a();
    });

    private readonly Func<string, bool> _alive;
    private readonly TimeSpan _fresh;
    private readonly Dictionary<(bool, string), (FolderAnswer Answer, DateTime At)> _done = [];
    private readonly Dictionary<(bool, string), object> _running = [];
    private readonly object _gate = new();

    internal HookFolderProbe(Func<string, bool>? slow = null, Func<bool, string, FolderAnswer>? look = null,
                             Action<Action>? background = null, Action<Action>? post = null, Func<string, bool>? alive = null,
                             TimeSpan? fresh = null)
    {
        _fresh = fresh ?? FreshFor;
        _alive = alive ?? Exists;
        _slow = slow ?? (static p => ThirdPartyHooks.OnNetwork(p));
        _look = look ?? ((w, p) => Look(w, p));
        _background = background ?? (a => Task.Run(a));
        _post = post ?? (a => a());
    }

    internal event Action<bool, string>? Answered;

    internal FolderAnswer Get(bool windower, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return default;
        if (!_slow(path)) return _look(windower, path);
        var key = (windower, path);
        object mine;
        bool gone = false;
        lock (_gate)
        {
            if (_done.TryGetValue(key, out var known))
            {
                if (DateTime.UtcNow - known.At < _fresh) return known.Answer;
                // a bounded look, so a share that's gone away costs the caller a moment, once
                if (known.Answer.Dir is null || AliveSoon(path))
                {
                    _done[key] = (known.Answer, DateTime.UtcNow);
                    return known.Answer;
                }
                _done.Remove(key);
                gone = true;
            }
            if (_running.ContainsKey(key)) return new(null, false, true);
            _running[key] = mine = new object();
        }
        if (gone) _post(() => Answered?.Invoke(windower, path));
        _background(() =>
        {
            FolderAnswer answer;
            try { answer = _look(windower, path); }
            catch { answer = default; }
            lock (_gate)
            {
                if (!_running.TryGetValue(key, out var current) || current != mine) return;
                _running.Remove(key);
                _done[key] = (answer, DateTime.UtcNow);
            }
            _post(() => Answered?.Invoke(windower, path));
        });
        return new(null, false, true);
    }

    // a path that's typed or browsed to again gets asked afresh, the share may be back
    internal void Forget(string? path)
    {
        if (path is null) return;
        lock (_gate)
            foreach (var windower in new[] { true, false })
            {
                _done.Remove((windower, path));
                _running.Remove((windower, path));
            }
    }

    private static bool Exists(string path) => HookFolder.Usable(path) is { } real && Directory.Exists(real);

    private bool AliveSoon(string path)
    {
        try
        {
            var look = Task.Run(() => _alive(path));
            return look.Wait(AliveWait) && look.Result;
        }
        catch { return false; }
    }

    // what Play would pick, but a folder on a share only counts once the probe's looked at it
    internal PlayTarget Target(LauncherSettings s, ThirdPartyHook hook)
    {
        bool windower = hook == ThirdPartyHook.Windower;
        var path = windower ? s.WindowerPath : s.AshitaPath;
        if (hook == ThirdPartyHook.None || !_slow(path ?? "")) return PlayTarget.For(s, null, hook);
        var answer = Get(windower, path);
        if (windower) return new(hook, answer.Dir, (s.WindowerProfile ?? "").Trim(), false);
        var boot = string.IsNullOrWhiteSpace(s.AshitaBootConfig) ? answer.LastBoot : s.AshitaBootConfig;
        return new(hook, answer.Dir, boot ?? "", false);
    }

    internal bool Checking(LauncherSettings s, ThirdPartyHook hook) => hook switch
    {
        ThirdPartyHook.Windower => Get(true, s.WindowerPath).Pending,
        ThirdPartyHook.Ashita => Get(false, s.AshitaPath).Pending,
        _ => false,
    };

    internal IReadOnlyList<string> WindowerProfileNames(string? path, string? dir) =>
        path is not null && _slow(path) ? Get(true, path).Profiles ?? [] : WindowerInstall.Profiles(dir);

    internal IReadOnlyList<(string File, string Shown)> AshitaBootNames(string? path, string? dir) =>
        path is not null && _slow(path) ? Get(false, path).Boots ?? [] : AshitaBootFiles.Names(dir);

    private FolderAnswer Look(bool windower, string path)
    {
        var usable = HookFolder.Usable(path);
        var dir = usable is not null && (windower ? WindowerInstall.IsFolder(usable) : AshitaInstall.IsFolder(usable)) ? usable : null;
        bool linked = windower && dir is not null && FolderPins.Linked(dir);
        // a share's files are read here too, since the UI thread mustn't go and read them
        if (dir is null || !_slow(path)) return new(dir, linked, false);
        return windower ? new(dir, linked, false, Profiles: WindowerInstall.Profiles(dir))
                        : new(dir, linked, false, Boots: AshitaBootFiles.Names(dir), LastBoot: AshitaInstall.LastUsedBootConfig(dir));
    }
}
