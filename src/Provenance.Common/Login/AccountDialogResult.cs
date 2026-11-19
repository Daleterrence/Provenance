namespace Provenance.Common.Login;

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
