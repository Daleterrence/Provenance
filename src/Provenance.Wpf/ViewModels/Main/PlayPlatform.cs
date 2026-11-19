using CommunityToolkit.Mvvm.ComponentModel;
using Provenance.Common.Game.Ashita;
using Provenance.Common.Game.ThirdParty;
using Provenance.Common.Game.Windower;
using Provenance.Common.Login;
using Provenance.Common.Settings;
using Provenance.Wpf.ViewModels.Shared;

namespace Provenance.Wpf.ViewModels.Main;

public sealed class PlayPlatform(PolSession pol, ISettingsService settings) : ObservableObject
{
    public string? Subtitle => pol.Platform switch
    {
        ThirdPartyHook.Windower => "WINDOWER LOADED",
        ThirdPartyHook.Ashita => "ASHITA LOADED",
        _ => null,
    };

    internal void Changed()
    {
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(HasChoices));
        OnPropertyChanged(nameof(ChoicesTitle));
    }

    public sealed record Choice(string Name, string Display, bool Current);

    public bool HasChoices => pol.Platform is ThirdPartyHook.Windower or ThirdPartyHook.Ashita;

    public string ChoicesTitle => pol.Platform == ThirdPartyHook.Ashita ? "Boot configuration" : "Windower profile";

    public string NoChoicesText => pol.Platform == ThirdPartyHook.Ashita
        ? "No boot configurations found" : "No Windower profiles found";

    public IReadOnlyList<Choice> Choices()
    {
        var target = PlayTarget.For(settings.Current, null);
        if (target.Platform == ThirdPartyHook.Ashita)
            return AshitaBootFiles.Names(target.Dir)
                .Select(n => new Choice(n.File, n.Shown, target.Is(n.File))).ToList();
        if (target.Platform == ThirdPartyHook.Windower)
        {
            var names = WindowerInstall.Profiles(target.Dir);
            var picked = target.Picked(names);
            return names.Select(n => new Choice(n, WindowerInstall.DisplayName(n, names), n == picked)).ToList();
        }
        return [];
    }

    public IReadOnlyList<MenuAction> ChoicesMenu()
    {
        var menu = new List<MenuAction> { new(ChoicesTitle, false), MenuAction.Separator };
        var choices = Choices();
        if (choices.Count == 0) menu.Add(new(NoChoicesText, false));
        foreach (var c in choices) menu.Add(new(c.Display, true, () => Pick(c.Name)) { Checked = c.Current });
        return menu;
    }

    public void Pick(string name)
    {
        var platform = pol.Platform;
        if (platform is not (ThirdPartyHook.Ashita or ThirdPartyHook.Windower)) return;
        settings.SaveChange(s =>
        {
            if (platform == ThirdPartyHook.Ashita) s.AshitaBootConfig = name;
            else s.WindowerProfile = name;
        });
    }
}
