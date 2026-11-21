using Provenance.Common.Game.Windower;

namespace Provenance.Common.Login;

public sealed record SessionOptions(bool Lean, string? ThirdPartyHookProfile, int HomeSlot = 0, IReadOnlyDictionary<string, WindowerAddonList>? WindowerLists = null,
                                    WindowerAddonList? WindowerShared = null, string? HandleKey = null, bool Plain = false);
