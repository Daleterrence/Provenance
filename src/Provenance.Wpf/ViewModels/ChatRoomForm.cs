namespace Provenance.Wpf.ViewModels;

// the viewer's Create Room form, Members/Purpose/Language 0 when not set
public sealed record ChatRoomForm(int Zone, string Name, int Occupancy, string? Password, int Members, int Purpose, int Language);
