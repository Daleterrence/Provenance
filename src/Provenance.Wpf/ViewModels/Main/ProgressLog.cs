using Provenance.Common.Login;
using Serilog;

namespace Provenance.Wpf.ViewModels.Main;

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
