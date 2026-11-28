using System.Diagnostics;
using System.Text.Json;
using Provenance.OtpHelper;
using Serilog;

namespace Provenance.Common.Otp;

public sealed record OnePasswordAccount(string Id, string Url, string Email);

public static class OnePassword
{
    private const string SettingsLink = "onepassword://settings/developers";
    private static readonly Dictionary<string, string> Env = new() { [OpCommand.AppUnlock] = "true" };

    public const string WaitText = "Getting the code from 1Password. Confirm with Windows Hello when it asks.";
    public const string OpenSettingsFailedText = "Windows didn't open 1Password.";

    internal static async Task<OtpFetchResult> FetchAsync(string item, string account, CancellationToken ct)
    {
        if (OtpSources.StartsWithDash(item)) return OtpSources.Problem("item starts with a dash", OtpSources.DashText, OtpFix.TypeInstead);
        using var found = await Task.Run(() => ToolFinder.Find(ToolFacts.OnePassword), ct);
        if (found.Path is not { } op) return OtpSources.ToolProblem(ToolFacts.OnePassword, found);
        try
        {
            var r = await OnePasswordHelper.RunAsync(op, OpRequest.Otp(item, account), Env, TimeSpan.FromSeconds(90), ct);
            return OtpSources.CodeFrom(r, err => ProblemFor(err, item));
        }
        finally { ToolInstaller.UpdateInBackground(ToolFacts.OnePassword); }
    }

    internal static (string Outcome, string Text, OtpFix Fix) ProblemFor(string err, string item)
    {
        bool Says(string s) => err.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (Says("No accounts configured for use with 1Password CLI") || Says("account is not signed in"))
            return ("integration off", "1Password's CLI integration is turned off.", OtpFix.OpenOnePasswordSettings);
        if (Says("cannot connect to 1Password app") || Says("LostConnectionToApp"))
            return ("app not reachable", "1Password isn't open.", OtpFix.TryAgain);
        if (Says("authorization prompt dismissed"))
            return ("prompt closed", "The 1Password prompt was closed.", OtpFix.TryAgain);
        if (Says("isn't an item"))
            return ("not an item", $"1Password has no item called \"{item}\".", OtpFix.TypeInstead);
        if (Says("More than one item matches"))
            return ("several items", $"More than one 1Password item is called \"{item}\".", OtpFix.TypeInstead);
        return (OtpSources.AndSaid("unrecognised error", err, item), "1Password didn't give the code.", OtpFix.TryAgain);
    }

    public static async Task<IReadOnlyList<OnePasswordAccount>?> AccountsAsync(string op, CancellationToken ct)
    {
        try
        {
            var r = await OnePasswordHelper.RunAsync(op, OpRequest.Accounts, Env, TimeSpan.FromSeconds(20), ct);
            if (r.Exit != 0)
            {
                Log.Information("Listing 1Password's accounts: exit {Exit}", r.Exit);
                return null;
            }
            return ParseAccounts(r.Out);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "Listing 1Password's accounts failed");
            return null;
        }
    }

    internal static IReadOnlyList<OnePasswordAccount>? ParseAccounts(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var list = new List<OnePasswordAccount>();
            foreach (var a in doc.RootElement.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object) continue;
                string Text(string name) => a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                var id = Text("account_uuid");
                if (OpCommand.IsAccountId(id) && list.All(x => x.Id != id)) list.Add(new OnePasswordAccount(id, Text("url"), Text("email")));
            }
            return list;
        }
        catch (JsonException) { return null; }
    }

    // 1Password mustn't start as admin, so explorer.exe hands the link to the user's own shell
    public static bool OpenSettings()
    {
        try
        {
            var psi = new ProcessStartInfo(AppPaths.ExplorerExe) { UseShellExecute = false };
            psi.ArgumentList.Add(SettingsLink);
            using var p = Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open 1Password's developer settings");
            return false;
        }
    }
}
