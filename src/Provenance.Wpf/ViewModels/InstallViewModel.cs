using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using Provenance.Common;
using Provenance.Common.Game;
using Provenance.Common.Install;
using Provenance.Common.Mirror;
using Provenance.Common.Patcher;
using Provenance.Common.PolFormat;
using Provenance.Wpf.Services;

namespace Provenance.Wpf.ViewModels;

// The mirror goes first (current files straight into the game folders), then SE's five parts as the fallback,
// unpacked here and installed with no installer windows
public partial class InstallViewModel : ObservableObject
{
    private const long GameDriveBytes = 16_500_000_000;       // the installed and updated game, with update temp files
    private const long DownloadDriveBytes = 15_500_000_000;   // the parts and their unpacked copy
    private const string MirrorFolder = "mirror";
    private const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001;
    private static readonly HttpClient MirrorHttp = new() { Timeout = TimeSpan.FromSeconds(60) };

    // Japanese installs work, but stay out of the list until a JP login has been seen working
    private const bool OfferJapanese = false;

    // the versions the installer offers, North American first, only ones Provenance can log in with
    private static readonly PolRegion[] RegionValues = new[] { PolRegion.UnitedStates, PolRegion.Europe, PolRegion.Japan }
        .Where(r => GameInstaller.CanInstall(r) && PolPaths.Supported(r) && (r != PolRegion.Japan || OfferJapanese)).ToArray();

    internal static Func<PolRegion> LocaleRegion { get; set; } = PolLocale.FromWindows;

    internal PolRegion _region;
    private readonly PolRegion _autoRegion;
    private readonly Action? _holdPrestarts, _releasePrestarts;
    private readonly GameInstaller _installer = new();
    private readonly DownloadMeter _meter;
    private CancellationTokenSource? _cts;
    private InstallFromMirror? _mirror;

    public InstallViewModel(PolRegion? region, Action? holdPrestarts = null, Action? releasePrestarts = null)
    {
        // a viewer that's already there decides, otherwise it's the player's pick
        ShowRegionChoice = region is not { } r || !RegionValues.Contains(r);
        // a version that isn't offered yet (Japanese, for now) leaves Auto on the first one that is
        var local = LocaleRegion();
        _autoRegion = RegionValues.Contains(local) ? local : RegionValues[0];
        RegionChoices = [$"Auto ({PolPaths.RegionName(_autoRegion)})", .. RegionValues.Select(PolPaths.RegionName)];
        _region = ShowRegionChoice ? _autoRegion : region!.Value;
        _holdPrestarts = holdPrestarts;
        _releasePrestarts = releasePrestarts;
        _meter = new DownloadMeter(text => DownloadText = text, text => TimeLeftText = text);
        // the MSIs' own default INSTALLDIR for these 32-bit packages
        InstallFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "PlayOnline");
        // the installer files run as admin, so they live in an admin-only folder (LockWorkFolder), never %TEMP%
        DownloadFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Provenance");
        TotalGib = $"{GameInstaller.TotalBytes(_region) / 1024.0 / 1024 / 1024:0.0} GiB";
        UpdateSpace();
    }

    public event Action? Installed;

    public bool ShowRegionChoice { get; }

    public IReadOnlyList<string> RegionChoices { get; }

    // fixed once a run's started, half an install of one region under the other's would be a mess
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChooseRegion))]
    private bool _regionLocked;

    public bool CanChooseRegion => !RegionLocked;

    [ObservableProperty] private int _regionIndex;

    partial void OnRegionIndexChanged(int value)
    {
        if (RegionLocked || value < 0 || value > RegionValues.Length) return;
        _region = value == 0 ? _autoRegion : RegionValues[value - 1];
        TotalGib = $"{GameInstaller.TotalBytes(_region) / 1024.0 / 1024 / 1024:0.0} GiB";
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GameFolderText))]
    private string _installFolder = "";

    public string GameFolderText => string.IsNullOrWhiteSpace(InstallFolder) ? "" :
        $"The game will be installed in {Path.Combine(InstallFolder, "SquareEnix")}.";

    [ObservableProperty] private string _downloadFolder = "";
    [ObservableProperty] private string _totalGib = "";
    [ObservableProperty] private string _freeSpaceText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _enoughSpace = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart), nameof(StepText))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _running;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private bool _done;

    public bool CanStart => !Running && !Done;

    [ObservableProperty] private string _startLabel = "DOWNLOAD AND INSTALL";
    [ObservableProperty] private string _stage = "Ready to install";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private double _downloadFraction;
    [ObservableProperty] private string _downloadText = "";
    [ObservableProperty] private string _timeLeftText = "";
    [ObservableProperty] private bool _stageIndeterminate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepText))]
    private int _currentStep;    // 0 download, 1 unpack, 2 install, 3 done

    // the mirror's files come ready to install, so that way has no unpack step
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepText))]
    private bool _fromMirror;

    public string StepText => !Running ? "" : CurrentStep switch
    {
        0 => $"Step 1 of {(FromMirror ? 2 : 3)}: Download",
        1 => "Step 2 of 3: Unpack",
        2 => FromMirror ? "Step 2 of 2: Install" : "Step 3 of 3: Install",
        _ => "Finishing",
    };

    // Continue tries the mirror again first, so a stop while unpacking can't promise the unpack
    internal static string StoppedText(int step) => step switch
    {
        0 => "The download was stopped. It'll carry on from where it left off.",
        1 => "Unpacking was stopped. The download is kept, so press Continue to carry on with the install.",
        _ => "The install was stopped before it finished. Press Continue to finish it.",
    };

    private bool CanStartInstall() => !Running && !Done && EnoughSpace;

    partial void OnInstallFolderChanged(string value) => UpdateSpace();
    partial void OnDownloadFolderChanged(string value) => UpdateSpace();

    private void UpdateSpace()
    {
        // a relative or empty path would resolve against this elevated process's working folder
        if (InstallFolder is not { } install || DownloadFolder is not { } download ||
            !Path.IsPathFullyQualified(install) || !Path.IsPathFullyQualified(download))
        {
            EnoughSpace = false;
            FreeSpaceText = "Choose full folder paths, such as C:\\Program Files (x86)\\PlayOnline.";
            return;
        }
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(install));
            if (root != null && root.StartsWith(@"\\", StringComparison.Ordinal))
            {
                EnoughSpace = true;
                FreeSpaceText = "Provenance can't check the free space on a network folder.";
            }
            else if (root != null)
            {
                var d = new DriveInfo(root);
                var dlRoot = string.IsNullOrWhiteSpace(download) ? null : Path.GetPathRoot(Path.GetFullPath(download));
                bool split = dlRoot != null && !dlRoot.StartsWith(@"\\", StringComparison.Ordinal) &&
                             !string.Equals(dlRoot, root, StringComparison.OrdinalIgnoreCase);
                static string Gib(long b) => (b / 1024.0 / 1024 / 1024).ToString("0.0");
                if (!split)
                {
                    EnoughSpace = d.AvailableFreeSpace >= GameInstaller.RequiredFreeBytes;
                    FreeSpaceText = $"There's {Gib(d.AvailableFreeSpace)} GiB free on {root}, and the install needs about {GameInstaller.RequiredFreeBytes / 1024.0 / 1024 / 1024:0} GiB.";
                }
                else
                {
                    long dlFree = new DriveInfo(dlRoot!).AvailableFreeSpace;
                    EnoughSpace = d.AvailableFreeSpace >= GameDriveBytes && dlFree >= DownloadDriveBytes;
                    FreeSpaceText = $"There's {Gib(d.AvailableFreeSpace)} GiB free on {root} for the game, which needs about {Gib(GameDriveBytes)} GiB. " +
                                    $"There's {Gib(dlFree)} GiB free on {dlRoot} for the download, which needs about {Gib(DownloadDriveBytes)} GiB.";
                }
            }
        }
        catch
        {
            EnoughSpace = false;
            FreeSpaceText = "Provenance can't read how much space is free on that drive.";
        }
    }

    [RelayCommand]
    private void BrowseInstall()
    {
        if (SettingsViewModel.PickFolder("Choose where to install FINAL FANTASY XI") is { } d) InstallFolder = d;
    }

    [RelayCommand]
    private void BrowseDownload()
    {
        if (SettingsViewModel.PickFolder("Choose where to keep the downloaded installer") is { } d) DownloadFolder = d;
    }

    // its files run as admin, so it's an admin-only ffxi-download folder in a folder only admins can change, both held
    // until the install ends. A stranger's folder with the same name gets skipped
    private InstallWorkFolder.WorkFolder LockWorkFolder(IEnumerable<string> ownNames)
    {
        var chosen = Path.TrimEndingDirectorySeparator(Path.GetFullPath(DownloadFolder));
        if (!PathSafety.OnLocalFixedDrive(chosen))
        {
            Log.Warning("Download folder {Folder} refused: it isn't on a fixed drive in this PC", chosen);
            throw Shown.Mark(new InvalidOperationException(
                "The download folder has to be on a drive in this PC, not a network or removable one. " +
                "Choose another folder, such as C:\\ProgramData\\Provenance."));
        }
        try
        {
            return InstallWorkFolder.HoldWorkFolder(chosen, ownNames.ToList(), PathSafety.AdminOnlyProblem)
                   ?? throw Shown.Mark(new InvalidOperationException($"Provenance couldn't make its own folder in {chosen}. Choose another download folder."));
        }
        catch (InstallWorkFolder.FolderRefusedException e)
        {
            Log.Warning("Download folder {Folder} refused: {Why}", chosen, e.Message);
            throw Shown.Mark(new InvalidOperationException(
                "The installer runs as admin, so its download folder has to be one only administrators can change. " +
                "Choose another folder, such as C:\\ProgramData\\Provenance."));
        }
    }

    private object? _barStep;

    // a retry takes bytes back, so within one step the bar holds until it's passed again. A new step starts it over
    private void ShowFraction(object step, double fraction)
    {
        if (!Equals(step, _barStep)) { _barStep = step; DownloadFraction = fraction; }
        else DownloadFraction = Math.Max(DownloadFraction, fraction);
    }

    // the bytes the held bar stands for, so the text under it doesn't drop back on a retry either
    private long Held(long done, long total) => Math.Clamp((long)(DownloadFraction * total), done, Math.Max(done, total));

    // the detail already has the sizes, so the meter only adds the time left. Each package is its own step, Windows
    // Installer's retries and rollback included
    private Progress<InstallProgress> InstallStepProgress() => new(p =>
    {
        Detail = p.Detail;
        StageIndeterminate = p.Fraction < 0;
        if (p.Fraction >= 0)
            ShowFraction(p.Detail.Contains("FINAL FANTASY XI") ? "ffxi" : p.Detail.Contains("PlayOnline") ? "viewer" : p.Detail, p.Fraction);
        if (p.TotalBytes > 0) _meter.Update(p.DoneBytes, p.TotalBytes, download: false, amount: false);
        else _meter.Clear();
    });

    private Progress<(long Done, long Total)> LocalProgress(string? detail = null)
    {
        var step = new object();
        return new(p =>
        {
            if (detail != null) Detail = detail;
            ShowFraction(step, p.Total > 0 ? (double)p.Done / p.Total : 0);
            _meter.Update(p.Done, p.Total, download: false);
        });
    }

    private static void RemoveDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (Exception e) { Log.Warning(e, "Could not remove {Dir}", dir); }
    }

    internal static void RemoveParts(string work, PolRegion region)
    {
        var unpacked = Path.Combine(work, "unpacked");
        if (Directory.Exists(unpacked)) RemoveDir(unpacked);
        foreach (var name in GameInstaller.PartsFor(region).SelectMany(p => new[] { p.FileName, p.FileName + ".part" }))
            try { File.Delete(Path.Combine(work, name)); } catch (Exception e) { Log.Warning(e, "Could not remove {Part}", name); }
    }

    // next is null when everything's already up to date
    private void Finished(string? next, IReadOnlyList<string> warnings)
    {
        CurrentStep = 3;
        StageIndeterminate = false;
        Stage = "Installed";
        Detail = (next is null
                     ? "PlayOnline Viewer and FINAL FANTASY XI have been successfully installed and updated."
                     : "PlayOnline Viewer and FINAL FANTASY XI have been successfully installed. " + next)
                 + (warnings.Count == 0 ? "" : " " + string.Join(" ", warnings));
        Done = true;
        Installed?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(CanStartInstall))]
    private async Task Start()
    {
        if (Running) return;
        Running = true;
        Done = false;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        // the download takes hours on a slow line, and Windows mustn't sleep in the middle of it
        SetThreadExecutionState(EsContinuous | EsSystemRequired);
        bool held = false;
        InstallWorkFolder.WorkFolder? workFolder = null;
        try
        {
            StageIndeterminate = true;
            Stage = "Preparing";
            Detail = "Checking this PC...";
            Log.Information("Install: region {Region}", _region);
            // my own waiting pol.exe would fail the check below, and none may start while the files go in
            if (_holdPrestarts is not null)
            {
                held = true;
                await Task.Run(() =>
                {
                    _holdPrestarts();
                    for (int i = 0; i < 10 && PolUpdates.ViewerRunning(); i++) Thread.Sleep(300);
                });
            }
            var running = Process.GetProcesses()
                .Where(p => p.ProcessName.Equals("pol", StringComparison.OrdinalIgnoreCase) ||
                            p.ProcessName.Equals("polboot", StringComparison.OrdinalIgnoreCase))
                .ToList();
            running.ForEach(p => p.Dispose());
            if (running.Count > 0)
                throw Shown.Mark(new InvalidOperationException("PlayOnline or FINAL FANTASY XI is running. Close it, then press Try again."));
            if (OtherVersionIn(InstallFolder, _region, PolPaths.TryGetInstallFolder) is { } clash)
                throw Shown.Mark(new InvalidOperationException(clash));
            // only from here, a refused start leaves the choice open
            RegionLocked = true;

            var parts = GameInstaller.PartsFor(_region).ToList();
            var ownNames = parts.SelectMany(p => new[] { p.FileName, p.FileName + ".part" }).Concat(new[] { "unpacked", "logs", MirrorFolder });
            Detail = "Checking the download folder...";
            workFolder = await Task.Run(() => LockWorkFolder(ownNames), ct);
            var work = workFolder.Path;
            if (await InstallFromMirrorAsync(work, ct)) return;
            var unpacked = Path.Combine(work, "unpacked");
            string root;
            for (int attempt = 1; ; attempt++)
            {
                CurrentStep = 0;
                Stage = "Downloading FINAL FANTASY XI";
                StageIndeterminate = false;
                long grandTotal = parts.Sum(p => p.Size);
                long doneBefore = 0;
                for (int i = 0; i < parts.Count; i++)
                {
                    Detail = $"Part {i + 1} of {parts.Count}";
                    var captured = doneBefore;
                    var progress = new Progress<long>(b =>
                    {
                        long overall = captured + b;
                        ShowFraction("parts", (double)overall / grandTotal);
                        _meter.Update(Held(overall, grandTotal), grandTotal);
                    });
                    await _installer.DownloadPartAsync(parts[i], work, progress, ct);
                    doneBefore += parts[i].Size;
                }

                CurrentStep = 1;
                Stage = "Unpacking";
                Detail = "Checking the downloaded files...";
                DownloadFraction = 0;
                try
                {
                    root = await _installer.ExtractAsync(work, unpacked, _region, LocalProgress("Unpacking the installer..."), ct,
                                                         LocalProgress());
                    break;
                }
                catch (DamagedPartException e) when (attempt < 2)
                {
                    // the bad part's been deleted, so it downloads again (once)
                    Log.Warning("{Part} failed its MD5 check before unpacking; downloading it again", e.Part);
                }
            }

            CurrentStep = 2;
            Stage = "Installing";
            _meter.Clear();
            IReadOnlyList<string> warnings;
            using (await UpdateLockAsync(ct))
            {
                warnings = await _installer.RunInstallAsync(root, InstallFolder, Path.Combine(work, "logs"), _region, InstallStepProgress(), ct);
                WriteFirstStartSettings(_region);
            }

            StageIndeterminate = true;
            Detail = "Removing the installer files...";
            await Task.Run(() => RemoveParts(work, _region), CancellationToken.None);

            // SE's files are from 2019, so the first sign-in updates POL and the first Play the game
            var updateBytes = await FirstUpdateSizeAsync(ct);
            Log.Information("Install finished in {Folder}; warnings: {Warnings}", InstallFolder, warnings);
            Finished(PartsNextText(updateBytes), warnings);
        }
        catch (OperationCanceledException)
        {
            Stage = "Cancelled";
            StartLabel = "CONTINUE";
            Detail = StoppedText(CurrentStep);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Install failed");
            Stage = "The install didn't finish";
            StartLabel = "TRY AGAIN";
            Detail = ErrorText.Of(ex, "Provenance ran into a problem during the install. Press Try again.");
        }
        finally
        {
            if (held && _releasePrestarts is not null)
            {
                try { _releasePrestarts(); }
                catch (Exception e) { Log.Warning(e, "Could not let pre-starts go again after the install"); }
            }
            workFolder?.Dispose();
            SetThreadExecutionState(EsContinuous);
            _meter.Clear();
            StageIndeterminate = false;
            Running = false;
        }
    }

    // verified current files from the mirror, the installer's steps without the copy, then patch.ver last. False sends
    // it to SE's parts, but a problem on this PC is an error
    internal async Task<bool> InstallFromMirrorAsync(string work, CancellationToken ct)
    {
        _mirror = null;
        if (MirrorLayout.RegionFolder(_region) is not { } mirrorRegion)
        {
            Log.Information("Install source: Square Enix's parts (the Provenance mirror has no copy of region {Region})", _region);
            return false;
        }
        FromMirror = true;
        CurrentStep = 0;
        Stage = "Preparing";
        Detail = InstallFromMirror.PrepareText;
        Log.Information("Install source: trying the Provenance mirror");
        var staging = Path.Combine(work, MirrorFolder);
        var mirror = _mirror = new InstallFromMirror(new MirrorClient(MirrorHttp, MirrorKeys.Trusted), mirrorRegion,
                                                 m => Log.Warning("Mirror install: {Message}", m));
        var progress = new Progress<InstallProgress>(p =>
        {
            Stage = p.Stage;
            Detail = p.Detail;
            StageIndeterminate = p.Fraction < 0;
            if (p.Fraction < 0) { _meter.Clear(); return; }
            ShowFraction((p.Stage, p.TotalBytes), p.Fraction);
            _meter.Update(Held(p.DoneBytes, p.TotalBytes), p.TotalBytes, download: p.Download);
        });
        async Task<bool> SquareEnixAsync(string reason)
        {
            Log.Information("Install source: Square Enix's parts ({Reason})", reason);
            FromMirror = false;
            StageIndeterminate = true;
            DownloadFraction = 0;
            _meter.Clear();
            await Task.Run(() => { if (Directory.Exists(staging)) RemoveDir(staging); }, CancellationToken.None);
            return false;
        }
        string? staged;
        IReadOnlyList<string> warnings;
        // the game files go straight into the game folders, so the whole way to patch.ver is under the lock
        using (await UpdateLockAsync(ct))
        {
            try
            {
                staged = await Task.Run(() => mirror.StageAsync(staging, InstallFolder, progress, ct), ct);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                return await SquareEnixAsync($"the Provenance mirror is unreachable: {ex.Message}");
            }
            catch (MirrorUnusableException ex)
            {
                return await SquareEnixAsync(ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error("Install source: the Provenance mirror, which failed: {Reason}", ex.Message);
                throw;
            }
            if (staged is null) return await SquareEnixAsync(mirror.SkipReason ?? "the mirror cannot serve this install");
            Log.Information("Install source: the Provenance mirror ({Products})",
                            string.Join(", ", mirror.Staged.Select(s => $"{s.Product.Name} {s.Version}{(s.Current ? "" : ", older than PlayOnline's")}")));

            CurrentStep = 2;
            Stage = "Installing";
            _meter.Clear();
            try
            {
                warnings = await _installer.RunInstallAsync(staged, InstallFolder, Path.Combine(work, "logs"), _region, InstallStepProgress(), ct, payloadInPlace: true);
                WriteFirstStartSettings(_region);
                mirror.WriteVersions();   // last, so a stopped install never claims a version
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error("Install source: the Provenance mirror, whose install steps failed: {Reason}", ex.Message);
                throw;
            }
        }

        StageIndeterminate = true;
        Detail = "Removing the installer files...";
        await Task.Run(() =>
        {
            RemoveDir(staged);
            // a parts run stopped before this one leaves gigabytes of SE's parts behind otherwise
            RemoveParts(work, _region);
        }, CancellationToken.None);

        Log.Information("Install finished in {Folder} from the Provenance mirror; warnings: {Warnings}", InstallFolder, warnings);
        var next = mirror.Staged.All(s => s.Current)
            ? null
            : "PlayOnline and the game finish updating the first time you log in and press Play.";
        Finished(next, warnings);
        return true;
    }

    internal static string PartsNextText(long? gameUpdateBytes) =>
        "PlayOnline updates itself the first time you log in, and the game updates the first time you press Play" +
        (gameUpdateBytes is long b && b > 0 ? $" ({RepairViewModel.Size(b)} to download)." : ".");

    // the way the first Play picks its files, reusing the mirror's game index. Null when that could come from POL (no
    // version from POL, or the mirror hasn't got it) or the mirror's unreachable
    private async Task<long?> FirstUpdateSizeAsync(CancellationToken ct)
    {
        if (MirrorLayout.UpdateFolder(_region, "ffxi") is not { } mirrorRegion || _mirror?.GameIndex is not { } index
            || PolPaths.TryGetInstallFolder(_region, PolPaths.FfxiProduct) is not { } ffxiDir
            || PolPaths.TryGetInstallFolder(_region, PolPaths.ViewerProduct) is not { } viewerDir)
            return null;
        Detail = "Checking how big the game's first update is...";
        var step = new object();
        var scanned = new Progress<(long Done, long Total)>(p =>
        {
            StageIndeterminate = false;
            ShowFraction(step, p.Total > 0 ? (double)p.Done / p.Total : 1);
            _meter.Update(p.Done, p.Total, download: false);
        });
        try
        {
            var local = new PolUpdater(PatchProduct.Ffxi(ffxiDir, viewerDir, _region)).LocalVersion();
            var bytes = await Task.Run(() => MirrorUpdater.DownloadSizeAsync(new MirrorClient(MirrorHttp, MirrorKeys.Trusted), mirrorRegion, "ffxi",
                ffxiDir, local, MirrorLayout.OwnFiles(_region, "ffxi"), index, scanned, ct), ct);
            Log.Information("First game update size: {Size}", bytes is { } n ? $"{n:N0} bytes from the Provenance mirror" : "the mirror has no usable copy");
            return bytes;
        }
        catch (Exception e)
        {
            Log.Information("First game update size not worked out: {Reason}", e.Message);
            return null;
        }
        finally { _meter.Clear(); }
    }

    // another region's viewer or game in the same folders would get written over, with both regions' keys pointing
    // there. This region's own install is fine, the installer carries on with it
    internal static string? OtherVersionIn(string installFolder, PolRegion region, Func<PolRegion, string, string?> registered,
                                           Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        var root = Path.Combine(installFolder, "SquareEnix");
        var products = new[] { (PolPaths.ViewerProduct, "PlayOnlineViewer", "pol.exe", "PlayOnline"), (PolPaths.FfxiProduct, "FINAL FANTASY XI", "FFXiMain.dll", "FINAL FANTASY XI") };
        foreach (var other in new[] { PolRegion.UnitedStates, PolRegion.Europe, PolRegion.Japan }.Where(r => r != region))
            foreach (var (code, folder, keyFile, name) in products)
                // a switched PC keeps the old region's keys, so it only counts with the files really there
                if (registered(other, code) is { } there && SameFolder(there, Path.Combine(root, folder)) && fileExists(Path.Combine(there, keyFile)))
                    return $"The {PolPaths.VersionName(other)} version of {name} is already installed in {there}. Choose another folder for the {PolPaths.VersionName(region)} version.";
        return null;
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                      StringComparison.OrdinalIgnoreCase);

    // the lock updates and repairs take, so another copy can't write into the same folders meanwhile
    private async Task<IDisposable> UpdateLockAsync(CancellationToken ct)
    {
        var waiting = new Progress<PatchProgress>(p =>
        {
            StageIndeterminate = true;
            Detail = p.Text;
        });
        return await Task.Run(() => PolUpdates.Lock(PolUpdates.Job.Install, ct, waiting), ct);
    }

    // SE's installer only writes 3 of the game's settings, and the game just closes without the rest
    public static void WriteFirstStartSettings(PolRegion region)
    {
        try
        {
            if (!FfxiRegistry.NeedsFirstStart(region)) return;
            int n = FfxiRegistry.WriteFirstStart(region);
            var d = FfxiRegistry.PrimaryDisplay();
            Log.Information("FINAL FANTASY XI first-start settings: {Count} values written for a {W}x{H} display", n, d.Width, d.Height);
        }
        catch (Exception e) { Log.Warning(e, "Could not write the FINAL FANTASY XI first-start settings"); }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (_cts is not { IsCancellationRequested: false } cts) return;
        cts.Cancel();
        Stage = "Stopping...";
        if (CurrentStep == 2) Detail = "The install will stop after its current step.";
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
}
