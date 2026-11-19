namespace Provenance.Common;

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
