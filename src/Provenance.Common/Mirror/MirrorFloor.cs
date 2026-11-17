namespace Provenance.Common.Mirror;

public static class MirrorFloor
{
    // The oldest pointer or build list a copy that remembers nothing takes. Never later than anything live on the
    // mirror, or every copy refuses that and goes to SE for the version
    public static readonly DateTimeOffset Published = new(2026, 10, 24, 0, 0, 0, TimeSpan.Zero);
}
