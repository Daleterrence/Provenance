using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Provenance.Common.Credentials;
using Provenance.Common.IO;
using Serilog;

namespace Provenance.Common.Social;

// what's changed since the chat log was last written whole, one encrypted batch per save
internal sealed class ChatJournal(string path)
{
    public const int MostBatches = 100;
    public const int MostBytes = 256 * 1024;

    // changing this would leave every saved journal unreadable
    private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("Provenance PlayOnline chat log journal v1");

    // Base is the hash of the log it was written on top of, Count how many lines there were before it
    internal sealed record Batch(string Base, int Count, Dictionary<int, ChatEntry> Changed, List<ChatEntry> Added);

    private long _length;
    private int _batches;
    // one whose lines never made it into memory is moved aside, never added to or deleted
    private bool _unread = true;

    public bool Full => _unread || _batches >= MostBatches || _length >= MostBytes || CutShort();

    public bool Leftover { get; private set; }

    public void Replay(string logHash, List<ChatEntry> entries)
    {
        (_length, _batches, _unread, Leftover) = (0, 0, false, false);
        if (!File.Exists(path)) return;
        byte[] bytes;
        try { bytes = SharedFile.ReadAllBytes(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning("{Log}: {Line}", "Messages", $"{Path.GetFileName(path)} couldn't be read ({e.GetType().Name}: {e.Message})");
            _unread = true;
            return;
        }
        int at = 0;
        while (bytes.Length - at > 4)
        {
            int size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at));
            if (size <= 0 || size > bytes.Length - at - 4) break;
            var batch = Read(bytes.AsSpan(at + 4, size).ToArray());
            if (batch is not null && batch.Base != logHash) break;
            // a whole batch that won't open or doesn't fit isn't known to be rubbish, so the journal's kept
            if (batch is null || batch.Count != entries.Count || batch.Changed.Keys.Any(i => i < 0 || i >= entries.Count))
            {
                _unread = true;
                break;
            }
            foreach (var (i, e) in batch.Changed) entries[i] = e;
            entries.AddRange(batch.Added);
            at += 4 + size;
            _batches++;
        }
        _length = at;
        Leftover = !_unread && at < bytes.Length && !Cut();
    }

    private static Batch? Read(byte[] cipher)
    {
        try
        {
            return Dpapi.Unprotect(cipher, Entropy) is { } plain &&
                   JsonSerializer.Deserialize<Batch>(plain) is { Base: not null, Changed: not null, Added: not null } batch &&
                   !batch.Added.Contains(null!) && !batch.Changed.ContainsValue(null!) ? batch : null;
        }
        catch (JsonException) { return null; }
    }

    public bool Append(Batch batch)
    {
        var name = Path.GetFileName(path);
        try
        {
            if (Dpapi.Protect(JsonSerializer.SerializeToUtf8Bytes(batch), Entropy) is not { } cipher)
            {
                Log.Warning("{Log}: {Line}", "Messages", $"{name} couldn't be encrypted, so it wasn't saved");
                return false;
            }
            var record = new byte[4 + cipher.Length];
            BinaryPrimitives.WriteInt32LittleEndian(record, cipher.Length);
            cipher.CopyTo(record, 4);
            using (var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
            {
                // anything past the last whole batch never finished being written, or is an older log's
                fs.SetLength(_length);
                fs.Seek(0, SeekOrigin.End);
                fs.Write(record);
                fs.Flush(flushToDisk: true);
            }
            _length += record.Length;
            _batches++;
            Leftover = false;
            return true;
        }
        catch (Exception e)
        {
            Log.Warning("{Log}: {Line}", "Messages", $"{name} couldn't be saved ({e.GetType().Name}: {e.Message})");
            // some of it may have landed
            Leftover = true;
            return false;
        }
    }

    public bool MakeWay()
    {
        if (!_unread) return true;
        try
        {
            if (File.Exists(path)) File.Move(path, path + "." + DateTime.Now.ToString("yyyyMMddHHmmss") + ".unreadable");
            _unread = false;
            return true;
        }
        catch (Exception e)
        {
            Log.Warning("{Log}: {Line}", "Messages", $"{Path.GetFileName(path)} couldn't be set aside ({e.GetType().Name}: {e.Message})");
            return false;
        }
    }

    public void Reset()
    {
        if (_unread) return;
        (_length, _batches) = (0, 0);
        Leftover = !Cut();
    }

    public void Tidy()
    {
        if (Leftover) Leftover = !Cut();
    }

    // what's past the last whole batch is no use to anyone, and can hold a line that's since been deleted
    private bool Cut()
    {
        try
        {
            if (!File.Exists(path)) return true;
            if (_length == 0)
            {
                File.Delete(path);
                return true;
            }
        }
        catch (Exception) { }
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > _length) fs.SetLength(_length);
            return true;
        }
        catch (Exception) { return false; }
    }

    // something else has cut the file down or taken it away, and adding to it would leave a hole
    private bool CutShort()
    {
        if (_length == 0) return false;
        try
        {
            var file = new FileInfo(path);
            return !file.Exists || file.Length < _length;
        }
        catch (Exception) { return true; }
    }
}
