namespace Provenance.Common.IO;

public static class ShortId
{
    public static string New(int length) => Guid.NewGuid().ToString("N")[..length];
}
