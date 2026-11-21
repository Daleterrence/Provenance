using System.Text.Json;
using Provenance.Common.Credentials;
using Serilog;

namespace Provenance.Common.IO;

// the entropy is part of the format, so changing one stops every old file opening
public sealed class EncryptedJsonFile<T>(string path, byte[] entropy, string logName, bool setAsideUnreadable = true)
    where T : class
{
    private bool _unread;

    public bool Unread => _unread;

    public string? Hash { get; private set; }

    public T? Load(bool wait = true)
    {
        Hash = null;
        _unread = false;
        if (!File.Exists(path)) return null;
        byte[] bytes;
        try { bytes = SharedFile.ReadAllBytes(path, wait ? 10 : 1); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning("{Log}: {Line}", logName, $"{Path.GetFileName(path)} couldn't be read ({e.GetType().Name}: {e.Message})");
            _unread = true;
            return null;
        }
        try
        {
            var plain = Dpapi.Unprotect(bytes, entropy);
            if (plain != null && JsonSerializer.Deserialize<T>(plain) is { } loaded)
            {
                Hash = FileHash.Sha256(bytes);
                return loaded;
            }
        }
        catch { }
        if (setAsideUnreadable)
        {
            try { File.Move(path, Aside()); } catch { _unread = true; }
        }
        return null;
    }

    private string Aside() => path + "." + DateTime.Now.ToString("yyyyMMddHHmmss") + ".unreadable";

    // one that was there but couldn't be read was never loaded, so it gets moved aside, not written over
    public bool MakeWay()
    {
        if (!_unread) return true;
        if (!setAsideUnreadable)
        {
            Log.Warning("{Log}: {Line}", logName, $"{Path.GetFileName(path)} hasn't been read, so it wasn't saved over");
            return false;
        }
        try
        {
            if (File.Exists(path)) File.Move(path, Aside());
            _unread = false;
            return true;
        }
        catch (Exception e)
        {
            Log.Warning("{Log}: {Line}", logName, $"{Path.GetFileName(path)} couldn't be set aside ({e.GetType().Name}: {e.Message})");
            return false;
        }
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
            if (!MakeWay()) return false;
            AtomicFile.Write(path, cipher);
            _unread = false;
            Hash = FileHash.Sha256(cipher);
            return true;
        }
        catch (Exception e)
        {
            Log.Warning("{Log}: {Line}", logName, $"{name} couldn't be saved ({e.GetType().Name}: {e.Message})");
            return false;
        }
    }
}
