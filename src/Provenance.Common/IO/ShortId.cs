namespace Provenance.Common.IO;

public static class ShortId
{
    // random hex, up to 32 characters
    public static string New(int length) => Guid.NewGuid().ToString("N")[..length];
}
