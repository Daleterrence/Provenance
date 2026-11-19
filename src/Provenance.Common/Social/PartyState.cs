namespace Provenance.Common.Social;

public enum PartyState { None, Member, Leader }

public static class PartyText
{
    public static string Tip(PartyState state) => state switch
    {
        PartyState.Member => "Currently in a party",
        PartyState.Leader => "Currently a party leader",
        _ => "",
    };
}
