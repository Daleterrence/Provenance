using System.Text.Json;
using Provenance.Common.Credentials;
using Serilog;

namespace Provenance.Common.IO;

// A DPAPI-encrypted JSON file. The entropy is part of the format, so changing one stops every old file opening.
public sealed class EncryptedJsonFile<T>(string path, byte[] entropy, string logName, bool setAsideUnreadable = true)
    where T : class
{
    // null when there's no file or it won't open. One that won't open is moved aside rather than let the next save
    // overwrite it (another Windows account, a damaged file)
    public T? Load()
    {
        if (!File.Exists(path)) return null;
        try
        {
            var plain = Dpapi.Unprotect(File.ReadAllBytes(path), entropy);
            if (plain != null && JsonSerializer.Deserialize<T>(plain) is { } loaded) return loaded;
        }
        catch { }
        if (setAsideUnreadable)
        {
            try { File.Move(path, path + "." + DateTime.Now.ToString("yyyyMMddHHmmss") + ".unreadable"); } catch { }
        }
        return null;
    }

    public bool Save(T value)
    {
        var name = Path.GetFileName(path);
        try
        {
            var cipher = Dpapi.Protect(JsonSerializer.SerializeToUtf8Bytes(value), entropy);
            if (cipher is null)
            {
                Log.Warning("{Log}: {Line}", logName, $"{name} couldn't be encrypted, so it wasn't saved");
                return false;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.Write(path, cipher);
            return true;
        }
        catch (Exception e)
        {
            Log.Warning("{Log}: {Line}", logName, $"{name} couldn't be saved ({e.GetType().Name}: {e.Message})");
            return false;
        }
    }
}
