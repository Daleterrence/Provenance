using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Provenance.Common.Install;

// msiexec /i /qn /norestart /l*v, but through msi.dll for real progress and a cancel that rolls back (killing
// msiexec /qn stops nothing)
public static class MsiInstaller
{
    public const int Success = 0;
    public const int RebootRequired = 3010;
    public const int RebootInitiated = 1641;
    public const int UserExit = 1602;
    public const int AlreadyRunning = 1618;

    // INSTALLSTATE_DEFAULT
    public static bool IsInstalled(string productCode) => MsiQueryProductStateW(productCode) == 5;

    public sealed record Report(string Action, double? Fraction, bool RollingBack);

    // blocks until Windows Installer's done (can be an hour), keep it off the UI thread
    public static int Install(string package, IReadOnlyDictionary<string, string> properties, string logPath,
                              IProgress<Report>? progress, CancellationToken ct, Action<string>? error = null)
    {
        var cmd = new StringBuilder("REBOOT=ReallySuppress");
        foreach (var (name, value) in properties)
            cmd.Append(' ').Append(name).Append("=\"").Append(value.Replace("\"", "\"\"")).Append('"');

        var state = new ProgressState();
        long lastReport = 0;
        string lastAction = "";
        void Send(Report r)
        {
            long now = Environment.TickCount64;
            if (r.Action == lastAction && now - lastReport < 100) return;
            lastReport = now;
            lastAction = r.Action;
            progress?.Report(r);
        }
        int Handler(IntPtr context, uint type, IntPtr record)
        {
            uint kind = type & 0xFF000000;
            // cancel only answers progress messages before the rollback starts, a rollback never gets cut short
            if (ct.IsCancellationRequested && !state.RollingBack && kind is MsgProgress or MsgActionData or MsgActionStart)
                return IdCancel;
            try
            {
                if (kind == MsgProgress && record != IntPtr.Zero && state.OnProgress(record) is { } r) Send(r);
                else if (kind == MsgActionData && state.OnActionData() is { } d) Send(d);
                else if (kind == MsgActionStart && record != IntPtr.Zero)
                    Send(state.OnActionStart(MsiPackage.RecordString(record, 2)));
                else if ((kind is MsgError or MsgFatalExit) && record != IntPtr.Zero
                         && FormatRecord(record) is { Length: > 0 } text)
                    error?.Invoke(text);
            }
            catch { }   // a report must never break the install
            return 0;   // no UI, so the installer takes its default action
        }

        InstallUiHandlerRecord handler = Handler;
        MsiSetInternalUI(InstallUiLevelNone, IntPtr.Zero);
        MsiSetExternalUIRecord(handler, Filter, IntPtr.Zero, IntPtr.Zero);
        MsiEnableLogW(LogVerbose, logPath, LogFlushEachLine);
        try
        {
            return (int)MsiInstallProductW(package, cmd.ToString());
        }
        finally
        {
            MsiSetExternalUIRecord(null, 0, IntPtr.Zero, IntPtr.Zero);
            MsiEnableLogW(0, null, 0);
            GC.KeepAlive(handler);
        }
    }

    private sealed class ProgressState
    {
        private long _total, _pos, _step;
        private bool _forward = true, _actionData, _script;
        private string _action = "";

        public bool RollingBack => !_forward;

        public Report OnActionStart(string description)
        {
            int t = description.IndexOf('[');
            if (t > 0) description = Regex.Replace(description[..t], @"\s*[A-Z]\S*:\s*$", "").TrimEnd().TrimEnd(':');
            if (description.Length > 0) _action = description.Trim();
            return Current();
        }

        public Report? OnProgress(IntPtr r)
        {
            // f1 0 = master reset (total, direction, f4 1 while writing the script), 1 = action info (each ActionData
            // moves f2 ticks when f3 is set), 2 = progress, 3 = more ticks to come
            int f1 = MsiRecordGetInteger(r, 1), f2 = MsiRecordGetInteger(r, 2), f3 = MsiRecordGetInteger(r, 3);
            switch (f1)
            {
                case 0:
                    _total = Math.Max(0, f2);
                    _forward = f3 == 0;
                    _pos = _forward ? 0 : _total;
                    _actionData = false;
                    _script = MsiRecordGetInteger(r, 4) == 1;
                    return Current();
                case 1:
                    _step = f2;
                    _actionData = f3 != 0;
                    return null;
                case 2:
                    if (_total <= 0) return null;
                    _pos += _forward ? f2 : -f2;
                    return Current();
                case 3:
                    _total += Math.Max(0, f2);
                    return null;
            }
            return null;
        }

        public Report? OnActionData()
        {
            if (!_actionData || _total <= 0) return null;
            _pos += _forward ? _step : -_step;
            return Current();
        }

        private Report Current() =>
            new(_action, _script || _total <= 0 ? null : Math.Clamp((double)_pos / _total, 0, 1), !_forward);
    }

    private static string FormatRecord(IntPtr record)
    {
        uint len = 0;
        var sb = new StringBuilder(1);
        MsiFormatRecordW(IntPtr.Zero, record, sb, ref len);
        sb = new StringBuilder((int)++len);
        return MsiFormatRecordW(IntPtr.Zero, record, sb, ref len) == 0 ? sb.ToString() : "";
    }

    private const int IdCancel = 2;
    private const int InstallUiLevelNone = 2;
    private const uint MsgFatalExit = 0x00000000, MsgError = 0x01000000, MsgActionStart = 0x08000000,
                       MsgActionData = 0x09000000, MsgProgress = 0x0A000000;
    // INSTALLLOGMODE FATALEXIT through COMMONDATA, and the log gets the same set as /l*v
    private const uint Filter = 0xFFF;
    private const uint LogVerbose = 0x1FFF;
    private const uint LogFlushEachLine = 0x2;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int InstallUiHandlerRecord(IntPtr context, uint messageType, IntPtr record);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiInstallProductW(string path, string cmd);

    [DllImport("msi.dll")]
    private static extern int MsiSetInternalUI(int level, IntPtr window);

    [DllImport("msi.dll")]
    private static extern uint MsiSetExternalUIRecord(InstallUiHandlerRecord? handler, uint filter, IntPtr context, IntPtr previous);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiEnableLogW(uint mode, string? file, uint attributes);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern int MsiQueryProductStateW(string productCode);

    [DllImport("msi.dll")]
    private static extern int MsiRecordGetInteger(IntPtr record, uint field);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiFormatRecordW(IntPtr install, IntPtr record, StringBuilder result, ref uint length);
}
