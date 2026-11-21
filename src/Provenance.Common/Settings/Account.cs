using System.Text.Json.Serialization;
using Provenance.Common.IO;

namespace Provenance.Common.Settings;

public sealed class Account
{
    public string Id { get; set; } = ShortId.New(8);
    public string DisplayName { get; set; } = "New account";

    public string? HandleName { get; set; }

    public string? HandleKey { get; set; }

    // the JSON name has to stay, saved accounts find their profile pictures by it
    [JsonPropertyName("FaceId")]
    public int ProfilePictureId { get; set; }

    public bool UseOtp { get; set; }

    public string OtpSource { get; set; } = "";

    public string OtpItem { get; set; } = "";

    public int HomeSlot { get; set; }

    public string OtpAccount { get; set; } = "";

    public List<AccountCharacter> Characters { get; set; } = [];

    public string? AutoLoginCharacter { get; set; }

    public string? AutoLoginWorld { get; set; }
}

public sealed record AccountCharacter(string Name, string? World);
