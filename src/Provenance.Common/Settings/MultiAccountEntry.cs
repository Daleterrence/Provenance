namespace Provenance.Common.Settings;

public sealed class MultiAccountEntry
{
    public string ProfileId { get; set; } = "";
    public bool Enabled { get; set; }
    public string? ThirdPartyHookProfile { get; set; }
    public string? Problem { get; set; }
    public bool CredentialsRefused { get; set; }
}
