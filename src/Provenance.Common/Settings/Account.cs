using System.Text.Json.Serialization;

namespace Provenance.Common.Settings;

// Id is the Credential Manager key so it can't change. DisplayName is the handle, the POL and SE IDs stay off screen
public sealed class Account
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string DisplayName { get; set; } = "New account";

    public string? HandleName { get; set; }

    // the active handle's Handle ID, for a login where the server doesn't say which handle was last active
    public string? HandleKey { get; set; }

    // profile picture, 0 = none. The JSON name has to stay, saved accounts find their pics by it
    [JsonPropertyName("FaceId")]
    public int ProfilePictureId { get; set; }

    // the code itself is asked for at every sign-in and never stored
    public bool UseOtp { get; set; }

    // "" (typed in each time), "bitwarden", "1password" or "keepassxc"
    public string OtpSource { get; set; } = "";

    // item name or id, or the entry URL for KeePassXC. Not a secret
    public string OtpItem { get; set; } = "";

    // the viewer folder's pub\homeNN this account's POL files go in, 0 till it's given one
    public int HomeSlot { get; set; }

    // the 1Password account that holds the item, "" lets op pick
    public string OtpAccount { get; set; } = "";
}
