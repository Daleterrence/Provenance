namespace Provenance.Common.Social;

// what the search server says about a friend's party. An alliance leader gets its own value here once the bit's known
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
