using Microsoft.Win32;
using Provenance.Common.Game.Windower;

namespace Provenance.Wpf.Services;

// what Windower writes into the registry for a profile. FFXI only reads it once Play's pressed, and another launch can
// have written its own profile's by then
public sealed record WindowerGameValues(WindowerProfile Profile, WindowerRegion Region, RegistryKey? Root)
{
    public IReadOnlyList<string> Apply() => WindowerGameRegistry.Apply(Profile, Region, Profile.Width, Profile.Height, Root);
}
