<#
.SYNOPSIS
    Builds a Provenance release with Velopack: Provenance-setup.exe, the full nupkg and the release feed files.
.EXAMPLE
    pwsh tools/pack-release.ps1 -Version 1.0.0-beta.1
.EXAMPLE
    pwsh tools/pack-release.ps1 -Version 1.0.0
.EXAMPLE
    pwsh tools/pack-release.ps1 -Version 1.0.1 -Out C:\Releases\1.0.1
#>
#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-(0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(\.(0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*)?$')]
    [string]$Version,
    [string]$Out
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'build-common.ps1')

$repo = Split-Path -Parent $PSScriptRoot
$src = Join-Path $repo 'src'
$wpf = Join-Path $src 'Provenance.Wpf\Provenance.Wpf.csproj'
$launcher = Join-Path $src 'Provenance.Launcher\Provenance.Launcher.csproj'
$assetsRoot = Find-ProvenanceAssets
$icon = Join-Path $assetsRoot 'app\provenance.ico'
$buildNative = Join-Path $repo 'native\build-native.ps1'
if (-not $Out) { $Out = Join-Path ([IO.Path]::GetTempPath()) "provenance-release\$Version" }
$Out = [IO.Path]::GetFullPath($Out)

$dirty = @(& git -C $repo status --porcelain)
if ($LASTEXITCODE -ne 0) { throw "git status failed in $repo." }
if ($dirty.Count) { throw "Commit or undo these first, a release only packs committed code:`n$($dirty -join "`n")" }
$commit = "$(& git -C $repo rev-parse HEAD)".Trim()

$committed = (Get-ProvenanceVersion).Version
if ($committed -cne $Version) {
    throw "Directory.Build.props says $committed. Set the version there to $Version and commit it first."
}

$repoPrefix = [IO.Path]::TrimEndingDirectorySeparator($repo) + '\'
if (($Out + '\').StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "-Out has to be outside the repo ($repo)." }
if ((Test-Path -LiteralPath $Out) -and @(Get-ChildItem -LiteralPath $Out -Force).Count) {
    throw "$Out is not empty. Everything in it goes up with the release, so use an empty or new folder."
}

$velopack = Get-VelopackVersion
$vpk = (Get-Command vpk -ErrorAction SilentlyContinue)?.Source
if (-not $vpk) { throw "vpk not found. Install it: dotnet tool install -g vpk --version $velopack" }
$help = & $vpk --skip-updates -h 2>&1 | Out-String
if ($help -notmatch 'Velopack CLI ([0-9][^\s,]*)') { throw "Could not read the vpk version from 'vpk -h'." }
if ($Matches[1] -ne $velopack) {
    throw "vpk is $($Matches[1]) but Provenance uses Velopack $velopack. Run: dotnet tool update -g vpk --version $velopack"
}

$natives = 'pol_hook.dll', 'Provenance Watcher.exe', 'Provenance - PlayOnline Uninstall.exe',
    'Provenance - FINAL FANTASY XI Uninstall.exe'
$channel = if ($Version.Contains('-')) { 'beta' } else { 'stable' }
Write-Host "vpk $velopack, release $Version ($channel channel) from $commit -> $Out"

$work = Join-Path ([IO.Path]::GetTempPath()) "provenance-pack-$Version-$PID"
$native = Join-Path $work 'native'
$publish = Join-Path $work 'publish'
try {
    New-Item -ItemType Directory -Force -Path $native, $publish, $Out | Out-Null

    & (Get-Process -Id $PID).Path -NoProfile -File $buildNative -OutDir $native
    if ($LASTEXITCODE -ne 0) { throw "build-native.ps1 failed (exit code $LASTEXITCODE)" }

    # launcher first: both bring the .NET runtime and Velopack.dll, and Provenance's copies should be the ones that stay
    foreach ($project in $launcher, $wpf) {
        $props = @("-p:Version=$Version")
        if ($project -eq $wpf) { $props += "-p:NativeToolsDir=$native" }
        & dotnet publish $project -c Release -r win-x86 --self-contained true @props -o $publish --nologo
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $(Split-Path -Leaf $project) (exit code $LASTEXITCODE)" }
    }

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

    & $vpk --skip-updates pack -u Provenance -v $Version -p $publish -e ProvenanceLauncher.exe -i $icon -r win-x86 `
        --packTitle Provenance --noPortable --channel $channel -o $Out
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed (exit code $LASTEXITCODE)" }

    $packed = "Provenance-$channel-Setup.exe", "Provenance-$Version-$channel-full.nupkg", "releases.$channel.json", "assets.$channel.json"
    foreach ($file in $packed) {
        if (-not (Test-Path -LiteralPath (Join-Path $Out $file))) { throw "vpk finished but $file is missing from $Out" }
    }
    # one name on every channel, so the download link never changes
    Move-Item -LiteralPath (Join-Path $Out $packed[0]) -Destination (Join-Path $Out 'Provenance-setup.exe')
} catch {
    if (Test-Path -LiteralPath $Out) { Get-ChildItem -LiteralPath $Out -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue }
    throw
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host "Release $Version from commit $commit is in $Out (not uploaded). Its statement comes next, signed on the draft before it's published."
Write-Host "Natives: built from native\ at $($commit.Substring(0, 7))"
if ($channel -eq 'beta') { Write-Host 'Channel: beta. Only copies on a beta will see it.' }
else { Write-Host 'Channel: stable. Every copy will see it, betas included.' }
Get-ChildItem -LiteralPath $Out -File | Sort-Object Name | ForEach-Object { '  {0,-40} {1,12:N0} bytes' -f $_.Name, $_.Length }
