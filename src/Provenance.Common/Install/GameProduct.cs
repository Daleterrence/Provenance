using Provenance.Common.PolFormat;

namespace Provenance.Common.Install;

public sealed record GameProduct
{
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public required string ShortName { get; init; }
    public required string FolderName { get; init; }
    public required string KeyFile { get; init; }
    public required int PatchId { get; init; }
    public required string Package { get; init; }
    public required string ShortcutProperty { get; init; }
    public required string Uninstaller { get; init; }
    public required string LogFile { get; init; }
    public required IReadOnlyList<(PolRegion Region, string Code)> ProductCodes { get; init; }
    public required Func<PolRegion, string> SettingsKey { get; init; }

    public string InstallValue => PatchId.ToString("0000");

    public string NoPackageText => $"The unpacked installer doesn't have a {Name} package in it.";

    public static readonly GameProduct Viewer = new()
    {
        Slug = "viewer", Name = "PlayOnline Viewer", ShortName = "PlayOnline", FolderName = "PlayOnlineViewer",
        KeyFile = "pol.exe", PatchId = 1000, Package = @"PlayOnline\PlayOnlineViewer.msi",
        ShortcutProperty = "MAKEPOLSHORTCUT", Uninstaller = "Provenance - PlayOnline Uninstall.exe", LogFile = "playonline.log",
        SettingsKey = PolPaths.ViewerKey,
        ProductCodes =
        [
            (PolRegion.UnitedStates, "{81784E3A-1BDA-4743-B5F8-04E59DC7E031}"),
            (PolRegion.Europe, "{F65CC5FB-3089-483C-8504-14EA5F58DD1D}"),
            (PolRegion.Japan, "{E58BCEA5-A722-4FCA-89D5-C89E18B4E3FE}"),
        ],
    };

    public static readonly GameProduct Game = new()
    {
        Slug = "ffxi", Name = "FINAL FANTASY XI", ShortName = "FINAL FANTASY XI", FolderName = "FINAL FANTASY XI",
        KeyFile = "FFXiMain.dll", PatchId = 1, Package = @"FINAL_FANTASY_XI\FINAL_FANTASY_XI.msi",
        ShortcutProperty = "MAKEFFXISHORTCUT", Uninstaller = "Provenance - FINAL FANTASY XI Uninstall.exe", LogFile = "ffxi.log",
        SettingsKey = PolPaths.FfxiKey,
        ProductCodes =
        [
            (PolRegion.UnitedStates, "{07EB4C8B-3869-49B4-8CF8-D6D9FB8C8026}"),
            (PolRegion.Europe, "{549BFF68-9B3D-46BE-88E3-BD4F7C6DEB36}"),
            (PolRegion.Japan, "{55DAC7CD-71AF-40DA-9BE0-AB5358EA0069}"),
        ],
    };

    public static readonly IReadOnlyList<GameProduct> All = [Viewer, Game];

    public static GameProduct? FromSlug(string slug) => All.FirstOrDefault(p => p.Slug == slug);

    public static bool Installs(PolRegion region) => Viewer.ProductCodes.Any(c => c.Region == region);

    public string ProductCode(PolRegion region) =>
        ProductCodes.FirstOrDefault(c => c.Region == region) is { Code: not null } c
            ? c.Code
            : throw new ArgumentOutOfRangeException(nameof(region), region, "Provenance doesn't install this region.");

    public string PackageIn(string root) => Path.Combine(root, Package);
}
