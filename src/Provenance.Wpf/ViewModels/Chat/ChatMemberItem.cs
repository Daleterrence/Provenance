namespace Provenance.Wpf.ViewModels.Chat;

public sealed record ChatMemberItem(string Name, bool Me, bool Master, bool Away, int Key = 0)
{
    public string Note => Away ? "Away" : Me ? "You" : "";
}
