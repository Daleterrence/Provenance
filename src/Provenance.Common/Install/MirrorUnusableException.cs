namespace Provenance.Common.Install;

public sealed class MirrorUnusableException(string message, IReadOnlyList<string> files) : Exception(message)
{
    public IReadOnlyList<string> Files { get; } = files;
}
