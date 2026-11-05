namespace Provenance.Common.Social;

public enum GroupOp
{
    Create,          // a = name
    Disband,
    Leave,
    SetClass,        // a = member index, b = class, key
    Comment,         // a = text
    Invite,          // a = friend row, b = subject, c = text, key
    AcceptInvite,    // a = message id, b = reply subject
    DeclineInvite,   // same as AcceptInvite
}
