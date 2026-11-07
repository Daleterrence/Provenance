namespace Provenance.Common.IO;

// reads that don't mind who else has the file open, or is about to delete it
public static class SharedFile
{
    public static FileStream OpenRead(string path, int bufferSize = 4096, FileOptions options = FileOptions.None) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize, options);

    public static string ReadAllText(string path)
    {
        using var fs = OpenRead(path);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd();
    }
}
