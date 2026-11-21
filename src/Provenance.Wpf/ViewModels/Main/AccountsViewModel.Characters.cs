using Provenance.Common.Social;

namespace Provenance.Wpf.ViewModels.Main;

public partial class AccountsViewModel
{
    private void LearnCharacters(string? profileId, PolSocialSnapshot snap)
    {
        if (profileId is null || Profiles.FirstOrDefault(p => p.Id == profileId) is not { } prof) return;
        if (Common.Login.AutoLogin.Learn(prof, snap)) PersistProfileList();
    }
}
