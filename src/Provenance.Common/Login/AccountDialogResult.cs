namespace Provenance.Common.Login;

// In edit mode an empty string means keep the saved value. ToString prints *** for every ID and password
public sealed record AccountDialogResult(string PolId, string PolPassword,
                                         string SeaId, string SeaPassword, bool UseOtp,
                                         string OtpSource = "", string OtpItem = "", string OtpAccount = "")
{
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("PolId = ***, PolPassword = ***, SeaId = ***, SeaPassword = ***, UseOtp = ").Append(UseOtp)
               .Append(", OtpSource = ").Append(OtpSource).Append(", OtpItem = ").Append(OtpItem)
               .Append(", OtpAccount = ").Append(OtpAccount);
        return true;
    }
}
