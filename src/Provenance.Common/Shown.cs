namespace Provenance.Common;

// a message written for the player shows as it is, anything unmarked gets the caller's own line. A flag, not a type,
// so every catch still sees the real exception
public static class Shown
{
    private const string Key = "provenance.shown";

    public static T Mark<T>(T ex) where T : Exception
    {
        ex.Data[Key] = true;
        return ex;
    }

    public static bool Is(Exception ex) => ex.Data.Contains(Key);
}
