using Provenance.Common.Game.ThirdParty;

namespace Provenance.Wpf.ViewModels.Settings;

internal sealed class AddonsPage
{
    public string? Message;
    public string Intro = "";
    public string? Problem;
    public ThirdPartyHook Platform = ThirdPartyHook.None;
    public bool UseGlobal = true, ListLocked;
    public string Key = "", Display = "";
    public string Branch = "stable";
    public List<RowData> Addons = [], Plugins = [], PolPlugins = [];
    public string? NoAddons, NoPlugins, NoPolPlugins;

    public static AddonsPage Empty(string message) => new() { Message = message };
}
