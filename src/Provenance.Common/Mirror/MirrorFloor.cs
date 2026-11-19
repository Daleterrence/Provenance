namespace Provenance.Common.Mirror;

public static class MirrorFloor
{
    // never later than anything live on the mirror, or every copy refuses that and goes to SE for the version
    public static readonly DateTimeOffset Published = new(2026, 11, 16, 19, 48, 0, TimeSpan.Zero);
}
