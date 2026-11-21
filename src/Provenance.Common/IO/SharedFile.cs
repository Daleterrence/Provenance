namespace Provenance.Common.IO;

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

    // half a second for whatever else has the file open to let go of it
    public static byte[] ReadAllBytes(string path, int tries = 10)
    {
        for (int i = 1; ; i++)
        {
            try
            {
                using var fs = OpenRead(path);
                var bytes = new byte[fs.Length];
                fs.ReadExactly(bytes);
                return bytes;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && i < tries && File.Exists(path)) { Thread.Sleep(50); }
        }
    }
}
