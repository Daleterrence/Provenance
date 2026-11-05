using Provenance.Common.Win32;

namespace Provenance.Common.Install;

public static class KeepAwake
{
    private const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001;

    public static void Hold() => Kernel32.SetThreadExecutionState(EsContinuous | EsSystemRequired);
    public static void Release() => Kernel32.SetThreadExecutionState(EsContinuous);
}
