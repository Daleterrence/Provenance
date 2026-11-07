namespace Provenance.Common.IO;

public static class FileDelete
{
    // for clean-up that mustn't turn into the error: a file that's in use or can't be touched stays where it is
    public static void Try(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
