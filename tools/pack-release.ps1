<#
.SYNOPSIS
    Builds a Provenance release with Velopack: Setup.exe, the full nupkg and the release feed files.

.DESCRIPTION
    Builds the native files from native\ at this commit (native\build-native.ps1, into a temporary folder), publishes
    Provenance.Launcher and Provenance.Wpf (Release, win-x86, self-contained) into one folder with -Version as the exe
    version, then runs vpk pack.
    -Version is SemVer 2 with no build part, so 1.0.0 or 1.0.0-beta.1, and has to be the version committed in
    src\Directory.Build.props. The exe's file version keeps just the numbers (1.0.0.0) and its product version gets the
    short commit (1.0.0-beta.1+1a2b3c4).
    It only packs committed code (a clean working tree) and needs an empty -Out outside the repo, by default
    %TEMP%\provenance-release\<version>. Nothing gets signed and nothing gets uploaded.
    A version with a suffix (1.0.0-beta.1) goes on the beta channel, one without (1.0.0) on stable. Copies on a beta
    look at both and take the newest, copies on a stable version only ever look at stable.
.EXAMPLE
    pwsh tools/pack-release.ps1 -Version 1.0.0-beta.1
.EXAMPLE
    pwsh tools/pack-release.ps1 -Version 1.0.0
.EXAMPLE
    pwsh tools/pack-release.ps1 -Version 1.0.1 -Out D:\Releases\1.0.1
#>
#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    # SemVer 2 without the +build part, which Velopack ignores when it compares versions
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-(0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(\.(0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*)?$')]
    [string]$Version,
    [string]$Out
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$src = Join-Path $repo 'src'
$wpf = Join-Path $src 'Provenance.Wpf\Provenance.Wpf.csproj'
$launcher = Join-Path $src 'Provenance.Launcher\Provenance.Launcher.csproj'
$icon = Join-Path $src 'Provenance.Wpf\Assets\provenance.ico'
$buildNative = Join-Path $repo 'native\build-native.ps1'
if (-not $Out) { $Out = Join-Path ([IO.Path]::GetTempPath()) "provenance-release\$Version" }
$Out = [IO.Path]::GetFullPath($Out)

# a release has to be a commit, so it can always be traced back and built again
$dirty = @(& git -C $repo status --porcelain)
if ($LASTEXITCODE -ne 0) { throw "git status failed in $repo." }
if ($dirty.Count) { throw "Commit or undo these first, a release only packs committed code:`n$($dirty -join "`n")" }
$commit = "$(& git -C $repo rev-parse HEAD)".Trim()

# the commit carries its own version, so a local build of it says the same as the release
$props = Join-Path $src 'Directory.Build.props'
$xml = [xml](Get-Content -LiteralPath $props -Raw)
# only the plain pair is read, so anything that could quietly win over it gets refused
$odd = @($xml.SelectNodes('//Version | //PackageVersion')).Count -gt 0 -or
       @($xml.SelectNodes('//VersionPrefix')).Count -ne 1 -or @($xml.SelectNodes('//VersionSuffix')).Count -gt 1 -or
       @($xml.SelectNodes('//VersionPrefix/@Condition | //VersionSuffix/@Condition | //PropertyGroup[VersionPrefix or VersionSuffix]/@Condition')).Count -gt 0
if ($odd) { throw "$props has to set the version with one plain VersionPrefix and at most one VersionSuffix." }
$prefix = "$($xml.SelectSingleNode('/Project/PropertyGroup/VersionPrefix').InnerText)".Trim()
$suffix = "$($xml.SelectSingleNode('/Project/PropertyGroup/VersionSuffix').InnerText)".Trim()
if (-not $prefix) { throw "No VersionPrefix in $props" }
$committed = if ($suffix) { "$prefix-$suffix" } else { $prefix }
if ($committed -cne $Version) {
    throw "src\Directory.Build.props says $committed. Set the version there to $Version and commit it first."
}

$repoPrefix = [IO.Path]::TrimEndingDirectorySeparator($repo) + '\'
if (($Out + '\').StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "-Out has to be outside the repo ($repo)." }
if ((Test-Path -LiteralPath $Out) -and @(Get-ChildItem -LiteralPath $Out -Force).Count) {
    throw "$Out is not empty. Everything in it goes up with the release, so use an empty or new folder."
}

function Get-VelopackVersion([string]$Project) {
    $m = Select-String -LiteralPath $Project -Pattern '<PackageReference Include="Velopack" Version="([^"]+)"'
    if (-not $m) { throw "No Velopack package reference in $Project" }
    $m.Matches[0].Groups[1].Value
}

# Update.exe comes from vpk and the update code from the package, so they have to be the same release
$velopack = Get-VelopackVersion $wpf
if ((Get-VelopackVersion $launcher) -ne $velopack) {
    throw "Provenance.Wpf and Provenance.Launcher use different Velopack versions, make them the same."
}
$vpk = (Get-Command vpk -ErrorAction SilentlyContinue)?.Source
if (-not $vpk) { throw "vpk not found. Install it: dotnet tool install -g vpk --version $velopack" }
$help = & $vpk --skip-updates -h 2>&1 | Out-String
if ($help -notmatch 'Velopack CLI ([0-9][^\s,]*)') { throw "Could not read the vpk version from 'vpk -h'." }
if ($Matches[1] -ne $velopack) {
    throw "vpk is $($Matches[1]) but Provenance uses Velopack $velopack. Run: dotnet tool update -g vpk --version $velopack"
}

$natives = 'pol_hook.dll', 'Provenance Watcher.exe', 'Provenance - PlayOnline Uninstall.exe',
    'Provenance - FINAL FANTASY XI Uninstall.exe'
# the app picks its channels by the same rule (SelfUpdate.ChannelSource), and so does the upload on GitHub
$channel = if ($Version.Contains('-')) { 'beta' } else { 'stable' }
Write-Host "vpk $velopack, release $Version ($channel channel) from $commit -> $Out"

$work = Join-Path ([IO.Path]::GetTempPath()) "provenance-pack-$Version-$PID"
$native = Join-Path $work 'native'
$publish = Join-Path $work 'publish'
try {
    New-Item -ItemType Directory -Force -Path $native, $publish, $Out | Out-Null

    # a fresh build from the committed sources, never whatever's sat in native\bin
    & (Get-Process -Id $PID).Path -NoProfile -File $buildNative -OutDir $native
    if ($LASTEXITCODE -ne 0) { throw "build-native.ps1 failed (exit code $LASTEXITCODE)" }

    # launcher first: both bring the .NET runtime and Velopack.dll, and Provenance's copies should be the ones that stay
    foreach ($project in $launcher, $wpf) {
        $props = @("-p:Version=$Version")
        if ($project -eq $wpf) { $props += "-p:NativeToolsDir=$native" }
        & dotnet publish $project -c Release -r win-x86 --self-contained true @props -o $publish --nologo
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $(Split-Path -Leaf $project) (exit code $LASTEXITCODE)" }
    }

    # Windows wants four numbers in the file version, the pre-release label only goes in the product version
    $numeric = "$($Version.Split('-')[0]).0"
    foreach ($file in @('Provenance.exe', 'ProvenanceLauncher.exe') + @($natives -like '*.exe' | ForEach-Object { "native\$_" })) {
        if (-not (Test-Path -LiteralPath (Join-Path $publish $file))) { throw "The publish folder has no $file" }
        $info = (Get-Item -LiteralPath (Join-Path $publish $file)).VersionInfo
        if ($info.FileVersion -ne $numeric) { throw "$file is version $($info.FileVersion), expected $numeric" }
        if ($info.ProductVersion -ne $Version -and -not $info.ProductVersion.StartsWith("$Version+")) {
            throw "$file has product version $($info.ProductVersion), expected $Version"
        }
    }
    foreach ($bin in $natives) {
        $shipped = Join-Path $publish "native\$bin"
        if (-not (Test-Path -LiteralPath $shipped)) { throw "The publish folder has no native\$bin" }
        if ((Get-FileHash -LiteralPath $shipped).Hash -ne (Get-FileHash -LiteralPath (Join-Path $native $bin)).Hash) {
            throw "native\$bin in the publish folder is not the one just built"
        }
    }

    # vpk also takes signing settings from VPK_* variables, so none of them get through
    $vpkVars = @(Get-ChildItem Env: | Where-Object Name -like 'VPK_*')
    foreach ($v in $vpkVars) { Remove-Item -LiteralPath "Env:\$($v.Name)" }
    try {
        & $vpk --skip-updates pack -u Provenance -v $Version -p $publish -e ProvenanceLauncher.exe -i $icon -r win-x86 `
            --packTitle Provenance --noPortable --channel $channel -o $Out
        if ($LASTEXITCODE -ne 0) { throw "vpk pack failed (exit code $LASTEXITCODE)" }
    } finally {
        foreach ($v in $vpkVars) { Set-Item -LiteralPath "Env:\$($v.Name)" -Value $v.Value }
    }

    $packed = "Provenance-$channel-Setup.exe", "Provenance-$Version-$channel-full.nupkg", "releases.$channel.json", "assets.$channel.json"
    foreach ($file in $packed) {
        if (-not (Test-Path -LiteralPath (Join-Path $Out $file))) { throw "vpk finished but $file is missing from $Out" }
    }
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host "Release $Version from commit $commit is in $Out (not signed, not uploaded)."
Write-Host "Natives: built from native\ at $($commit.Substring(0, 7))"
if ($channel -eq 'beta') { Write-Host 'Channel: beta. Only copies on a beta will see it.' }
else { Write-Host 'Channel: stable. Every copy will see it, betas included.' }
Get-ChildItem -LiteralPath $Out -File | Sort-Object Name | ForEach-Object { '  {0,-40} {1,12:N0} bytes' -f $_.Name, $_.Length }
