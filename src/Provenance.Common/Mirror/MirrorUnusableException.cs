namespace Provenance.Common.Mirror;

public sealed class MirrorUnusableException(string message, IReadOnlyList<string> files) : Exception(message)
{
    public IReadOnlyList<string> Files { get; } = files;
}
