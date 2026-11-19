using System.Runtime.CompilerServices;
using System.Text;

namespace Provenance.Common;

internal static class CodePages
{
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Register() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
}
