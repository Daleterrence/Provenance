using System.Runtime.InteropServices;

namespace Provenance.Common.Install;

public static class KeepAwake
{
    private const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001;

    public static void Hold() => SetThreadExecutionState(EsContinuous | EsSystemRequired);
    public static void Release() => SetThreadExecutionState(EsContinuous);

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
}
