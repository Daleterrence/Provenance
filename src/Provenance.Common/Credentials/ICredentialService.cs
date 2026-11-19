namespace Provenance.Common.Credentials;

public interface ICredentialService
{
    PolAccount? LoadPolAccount(string profileId);

    SquareEnixAccount? LoadSquareEnixAccount(string profileId);

    bool HasUsableDetails(string profileId);

    bool HasEntries(string profileId);

    (string? PolId, string? SeaId) ReadIds(string profileId);

    string? ReadPolId(string profileId);

    void SavePolAccount(string profileId, PolAccount account);

    void SaveSquareEnixAccount(string profileId, SquareEnixAccount account);

    void DeletePolAccount(string profileId);

    void DeleteSquareEnixAccount(string profileId);

    void DeleteProfile(string profileId);
}

public sealed record PolAccount(string PolId, string Password)
{
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("PolId = ***, Password = ***");
        return true;
    }
}

public sealed record SquareEnixAccount(string Username, string Password)
{
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Username = ***, Password = ***");
        return true;
    }
}
