using Provenance.Common.Game.ThirdParty;
using Provenance.Common.Game.Windower;

namespace Provenance.Common.Login;

public sealed record SessionOptions(bool Lean, string? ThirdPartyHookProfile, int HomeSlot = 0, IReadOnlyDictionary<string, WindowerAddonList>? WindowerLists = null,
                                    WindowerAddonList? WindowerShared = null, string? HandleKey = null, bool Plain = false,
                                    string? Character = null, string? CharacterWorld = null, bool AcceptLicence = false, int? CharacterSlot = null,
                                    ThirdPartyHook? Platform = null)
{
    public bool TakesOver => AcceptLicence || Character is not null || CharacterSlot is not null;

    public IReadOnlyList<string> WindowerLeaveOut => TakesOver ? [AutoLogin.WindowerAddon] : [];

    public IReadOnlyList<string> AshitaLeaveOut => TakesOver ? [AutoLogin.AshitaAddon] : [];
}
