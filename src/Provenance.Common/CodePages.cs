using System.Runtime.CompilerServices;
using System.Text;

namespace Provenance.Common;

internal static class CodePages
{
    // registers the code pages once, before anything in Common can ask for one
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Register() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
}
