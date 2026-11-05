namespace Provenance.Common.Credentials;

// Provenance:POL:<profile id> and Provenance:SEA:<profile id> in Credential Manager. Loads and saves need the wrap
// key and throw CredentialKeyUnavailableException (user text) when it won't open
public interface ICredentialService
{
    // decrypts the password, so only for a sign-in or a save, never just to test it
    PolAccount? LoadPolAccount(string profileId);

    SeaAccount? LoadSeaAccount(string profileId);

    // header test only, so an entry wrapped with a lost key still counts (its load gives null at sign-in)
    bool HasUsableDetails(string profileId);

    (string? PolId, string? SeaId) ReadIds(string profileId);

    string? ReadPolId(string profileId);

    void SavePolAccount(string profileId, PolAccount account);

    void SaveSeaAccount(string profileId, SeaAccount account);

    void DeletePolAccount(string profileId);

    void DeleteSeaAccount(string profileId);

    void DeleteProfile(string profileId);
}

// ToString prints *** for the ID and the password
public sealed record PolAccount(string PolId, string Password)
{
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("PolId = ***, Password = ***");
        return true;
    }
}

public sealed record SeaAccount(string Username, string Password)
{
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Username = ***, Password = ***");
        return true;
    }
}
