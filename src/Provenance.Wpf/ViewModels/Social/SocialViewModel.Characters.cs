using Provenance.Common.Social;

namespace Provenance.Wpf.ViewModels.Social;

public partial class SocialViewModel
{
    private CharacterNames? _characterNames;
    private CharacterLookups? _lookups;

    internal CharacterNames CharacterNames
    {
        get => _characterNames ??= new CharacterNames();
        set
        {
            _characterNames = value;
            _lookups = null;
            _friendsSignature = "";
        }
    }

    private CharacterLookups Lookups => _lookups ??= new CharacterLookups(CharacterNames);

    private void ApplyCharacters(PolSocialSnapshot snap)
    {
        var lookups = Lookups;
        lookups.Answer(snap.CharacterToken, snap.CharacterState, snap.Character);
        lookups.Waiting(snap.CharacterToken, snap.ProfileServerBusy, DateTime.UtcNow);
        if (snap.FriendsLoaded) lookups.Seen(snap.Friends);
        if (GameRunning || _pol.Pid is not { } pid || lookups.Next(DateTime.UtcNow) is not { } game) return;
        if (!SocialRequests.RequestCharacter(pid, lookups.Token, game.Ctsid, game.Ctid)) lookups.Failed();
    }

    private (string Long, string Short) Playing(PolFriend f)
    {
        if (!f.Online || f.Pending || f.Game is not { } game) return ("", "");
        var name = CharacterNames.Get(game.CharacterKey)?.Name ?? "";
        return PlayingText.For(name.Length > 0 ? Filtered(name) : "", FfxiWorlds.Name(game.World));
    }
}
