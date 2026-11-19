namespace Provenance.Common.IO;

public static class FileDelete
{
    public static void Try(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
