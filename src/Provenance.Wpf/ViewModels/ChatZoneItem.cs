using Provenance.Common.Social;

namespace Provenance.Wpf.ViewModels;

public sealed record ChatZoneItem(ChatZone Zone)
{
    public string Name => Zone.Name;
    public string Group => Zone.Group;
    public string Counts => $"Rooms: {Zone.Rooms}   Users: {Zone.People}";
}
