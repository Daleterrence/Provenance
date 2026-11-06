namespace Provenance.Common.Social;

// the pair that picks out an FFXI character (the profile's z_ctsid and z_ctid)
public readonly record struct CharacterId(uint Ctsid, ulong Ctid)
{
    public string Key => $"{Ctsid}:{Ctid}";
}
