using Provenance.Common.Game.Windower;

namespace Provenance.Common.Login;

// Lean is POL_LEAN (a login with no social side) and HomeSlot is POL_HOME, the account's pub\homeNN. HandleKey is
// POL_HANDLE, the Handle ID of the handle the account last had active
public sealed record SessionOptions(bool Lean, string? ThirdPartyHookProfile, int HomeSlot = 0, IReadOnlyDictionary<string, WindowerAddonList>? WindowerLists = null,
                                    WindowerAddonList? WindowerShared = null, string? HandleKey = null);
