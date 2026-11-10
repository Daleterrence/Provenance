namespace Provenance.Common.Social;

public readonly record struct CharacterId(uint Ctsid, ulong Ctid)
{
    public string Key => $"{Ctsid}:{Ctid}";
}
