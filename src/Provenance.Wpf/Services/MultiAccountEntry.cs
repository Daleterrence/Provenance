namespace Provenance.Wpf.Services;

public sealed class MultiAccountEntry
{
    public string ProfileId { get; set; } = "";
    public bool Enabled { get; set; }
    // null is none picked, which a single login takes as the Windower/Ashita tab's and multi-account mode refuses
    public string? ThirdPartyHookProfile { get; set; }
    // why a run turned it off. Never retried by itself, three refusals lock the account
    public string? Problem { get; set; }
    public bool CredentialsRefused { get; set; }
}
