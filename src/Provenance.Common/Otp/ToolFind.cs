using Microsoft.Win32.SafeHandles;

namespace Provenance.Common.Otp;

public enum FindState { Found, NotInstalled, CantCheck, TooOld }

public sealed class ToolFind : IDisposable
{
    private readonly SafeFileHandle? _handle;

    public ToolFind(string path, SafeFileHandle? handle = null)
    {
        Path = path;
        _handle = handle;
        State = FindState.Found;
    }

    public ToolFind(FindState notFound) => State = notFound;

    public string? Path { get; }
    public FindState State { get; }

    public void Dispose() => _handle?.Dispose();
}
