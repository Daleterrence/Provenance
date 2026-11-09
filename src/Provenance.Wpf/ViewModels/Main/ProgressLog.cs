using Provenance.Common.Login;
using Serilog;

namespace Provenance.Wpf.ViewModels.Main;

// a stage can report the same line thousands of times a minute, so only a change gets logged
internal sealed class ProgressLog
{
    private string? _stage, _detail;

    public bool Write(LaunchProgress p)
    {
        if (_stage == p.Stage && _detail == p.Detail) return false;
        _stage = p.Stage;
        _detail = p.Detail;
        Log.Information("progress: {Stage}: {Detail}", p.Stage, p.Detail);
        return true;
    }
}
