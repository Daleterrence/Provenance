namespace Provenance.Wpf.ViewModels.Chat;

public sealed record ChatRoomForm(int Zone, string Name, int Occupancy, string? Password, int Members, int Purpose, int Language);
