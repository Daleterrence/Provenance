using Microsoft.Win32.SafeHandles;

namespace Provenance.Wpf.Services;

// Path is held open (no writes, no deletes) until this is disposed, so what runs is the file that was checked
public sealed class ToolFind(string? path, bool cantCheck = false, SafeFileHandle? handle = null, bool tooOld = false) : IDisposable
{
    public string? Path { get; } = path;
    public bool CantCheck { get; } = cantCheck;
    public bool TooOld { get; } = tooOld;

    public void Dispose() => handle?.Dispose();
}
