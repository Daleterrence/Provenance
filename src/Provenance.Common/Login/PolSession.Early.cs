using System.Diagnostics;
using Provenance.Common.Game;
using Provenance.Common.Game.Ashita;
using Provenance.Common.Game.ThirdParty;
using Provenance.Common.Game.Windower;
using Provenance.Common.Patcher;
using Provenance.Common.PolFormat;
using Serilog;

namespace Provenance.Common.Login;

public sealed partial class PolSession
{
    internal Func<string, bool> BuildSupported { get; init; } = dir => SignedBuilds.Check(dir) == ViewerBuild.State.Supported;
    internal Func<string, bool> QueuePending { get; init; } = ViewerQueue.QueuedWorkPending;
    internal TimeSpan EarlyEndGrace { get; init; } = TimeSpan.FromSeconds(3);
    internal EarlyStart.Chain? EarlyChain { get; init; }

    private bool ThirdPartyLogin => Platform is ThirdPartyHook.Ashita or ThirdPartyHook.Windower;

    // Windower and Ashita get their pol.exe going inside the viewer update's lock and it's handed its login once
    // everything else is ready
    private sealed class EarlyLogin(PolSession owner, LoginRequest request, string viewerDir, string polExe, string launcherExe, CancellationToken ct,
                                    bool ahead = false)
    {
        private readonly HeldProgress _voice = new(request.Progress);
        private readonly WindowerUpdateAnswers _answers = new();
        private EarlyStart? _current;
        private bool _restarted;

        internal bool Active => _current is not null;

        internal string? LaunchId { get; private set; }

        internal WindowerUpdateAnswers Answers => _answers;

        private bool Wanted => owner.ThirdPartyLogin && !owner.Prestarter.Holding;

        // Large Address Aware takes the update lock itself, so it goes in before this copy holds it for the check
        internal void BeforeLock()
        {
            if (Wanted) owner._watcher.BeforeStart(viewerDir, "before the login");
        }

        internal void OnLocked(bool noneQueued)
        {
            if (!noneQueued || !Wanted || ct.IsCancellationRequested) return;
            if (owner.QueuePending(viewerDir) || !owner.BuildSupported(viewerDir)) return;
            lock (owner._gate) LaunchId = owner.Credentials.NewLaunchId();
            Begin();
        }

        private void Begin()
        {
            owner.Windower.Forget();
            var psi = Watcher.LauncherStart(launcherExe, polExe);
            HookEnvironment.SetPrestart(psi.Environment, loginComing: true);
            var options = request.Options;
            var launchId = LaunchId!;
            bool ashita = owner.Platform == ThirdPartyHook.Ashita;
            EarlyStart.Chain chain = owner.EarlyChain ?? (ashita
                ? (progress, token, hookStarted) => AshitaLauncher.StartViaAshita(owner._settings, owner._watcher, psi, polExe, launchId,
                                                                                  options.ThirdPartyHookProfile, progress, token, hookStarted)
                : (progress, token, _) => owner.Windower.Start(psi, polExe, launchId, options, progress, token, _answers));
            // an early start before the details has no account to name yet, and the login's claim has to stay till it has
            Action<ProcessStamp> record = ahead ? owner.Records.Park : stamp => owner.Records.Save(stamp, request.ProfileId);
            _current = new EarlyStart(chain, _voice, owner._pol, record, owner.Records.Clear,
                                      owner.EarlyEndGrace, ashita ? Watcher.PolDidNotStart : Watcher.WindowerDidNotStart, ct);
            _current.Start();
        }

        // the start's lines and Windower's notice show once the viewer check and its update bar are done
        internal void Release(bool withLine = true) => _voice.Release(withLine);

        internal string? Failed => _current?.Failure;

        internal void Drop() => _voice.Drop();

        internal bool End()
        {
            var current = _current;
            _current = null;
            return current?.End() ?? false;
        }

        // null once progress has said why
        internal int? Take(ProcessStartInfo psi, CancellationToken token)
        {
            var progress = request.Progress;
            while (true)
            {
                var early = _current!;
                if (!early.Wait(token) || token.IsCancellationRequested)
                {
                    progress?.Report(LaunchProgress.Cancelled(End()));
                    return null;
                }
                if (early.Failure is { } why)
                {
                    End();
                    progress?.Report(LaunchProgress.Failed(why));
                    return null;
                }
                if (early.HandOver(psi.Environment))
                {
                    if (early.Hook is { } hook) owner.HookTracker.Track(hook);
                    _current = null;
                    return early.Pid;
                }
                End();
                if (_restarted)
                {
                    progress?.Report(LaunchProgress.Failed(owner.Platform == ThirdPartyHook.Ashita ? Watcher.PolDidNotStart : Watcher.WindowerDidNotStart));
                    return null;
                }
                _restarted = true;
                Log.Warning("The waiting pol.exe was ended by something else before it got its login, starting it again");
                owner._watcher.BeforeStart(viewerDir, "before starting again");
                Begin();
            }
        }
    }
}
