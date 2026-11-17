using CommunityToolkit.Mvvm.ComponentModel;
using Provenance.Common.Game.Ashita;
using Provenance.Common.Game.ThirdParty;
using Provenance.Common.Game.Windower;
using Provenance.Common.Login;
using Provenance.Common.Settings;
using Provenance.Wpf.ViewModels.Settings;
using Provenance.Wpf.ViewModels.Shared;

namespace Provenance.Wpf.ViewModels.Main;

// what Play starts the game through, and the Windower profile or Ashita boot configuration it'll use
public sealed class PlayPlatform : ObservableObject
{
    private readonly PolSession pol;
    private readonly ISettingsService settings;

    internal HookFolderProbe Folders { get; }

    internal PlayPlatform(PolSession pol, ISettingsService settings, HookFolderProbe? folders = null)
    {
        this.pol = pol;
        this.settings = settings;
        Folders = folders ?? HookFolderProbe.Shared;
    }

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

    // the same pick Play uses: Ashita falls back to the last config Ashita ran with, Windower's "" is the unnamed one
    public IReadOnlyList<Choice> Choices()
    {
        var target = Folders.Target(settings.Current, settings.Current.ThirdPartyHookPlatform);
        if (target.Platform == ThirdPartyHook.Ashita)
            return Folders.AshitaBootNames(settings.Current.AshitaPath, target.Dir)
                .Select(n => new Choice(n.File, n.Shown, target.Is(n.File))).ToList();
        if (target.Platform == ThirdPartyHook.Windower)
        {
            var names = Folders.WindowerProfileNames(settings.Current.WindowerPath, target.Dir);
            var picked = target.Picked(names);
            return names.Select(n => new Choice(n, WindowerInstall.DisplayName(n, names), n == picked)).ToList();
        }
        return [];
    }

    public IReadOnlyList<MenuAction> ChoicesMenu()
    {
        var menu = new List<MenuAction> { new(ChoicesTitle, false), MenuAction.Separator };
        var choices = Choices();
        if (choices.Count == 0)
            menu.Add(new(Folders.Checking(settings.Current, settings.Current.ThirdPartyHookPlatform) ? HookFolder.CheckingText : NoChoicesText, false));
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
