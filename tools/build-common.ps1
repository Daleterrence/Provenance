<#
.SYNOPSIS
    The version, the assets folder, Velopack's version and the C++ tools, looked up the same way for every build script
    and CI. Dot-source it, it only defines functions.
#>

function Get-ProvenanceVersion {
    $props = Join-Path (Split-Path -Parent $PSScriptRoot) 'Directory.Build.props'
    $xml = [xml](Get-Content -LiteralPath $props -Raw)
    # only the plain pair is read, so anything that could quietly win over it gets refused
    $odd = @($xml.SelectNodes('//Version | //PackageVersion')).Count -gt 0 -or
           @($xml.SelectNodes('//VersionPrefix')).Count -ne 1 -or @($xml.SelectNodes('//VersionSuffix')).Count -gt 1 -or
           @($xml.SelectNodes('//VersionPrefix/@Condition | //VersionSuffix/@Condition | //PropertyGroup[VersionPrefix or VersionSuffix]/@Condition')).Count -gt 0
    if ($odd) { throw "$props has to set the version with one plain VersionPrefix and at most one VersionSuffix." }
    $prefix = "$($xml.SelectSingleNode('/Project/PropertyGroup/VersionPrefix').InnerText)".Trim()
    $suffix = "$($xml.SelectSingleNode('/Project/PropertyGroup/VersionSuffix').InnerText)".Trim()
    if ($prefix -notmatch '^\d+\.\d+\.\d+$') { throw "$props has no usable VersionPrefix ($prefix)" }
    [pscustomobject]@{ Prefix = $prefix; Suffix = $suffix; Version = $(if ($suffix) { "$prefix-$suffix" } else { $prefix }) }
}

# the same places Directory.Build.props looks
function Find-ProvenanceAssets {
    $repo = Split-Path -Parent $PSScriptRoot
    $found = @($env:PROVENANCE_ASSETS, (Join-Path $repo '..\assets'), (Join-Path $repo '..\..\assets')) |
        Where-Object { $_ -and (Test-Path -LiteralPath (Join-Path $_ 'app\provenance.ico')) } | Select-Object -First 1
    if (-not $found) { throw "Provenance's images aren't in this repo. Put the assets folder next to the repo, or set PROVENANCE_ASSETS to where it is." }
    [IO.Path]::GetFullPath($found)
}

function Get-VelopackVersion {
    $packages = Join-Path (Split-Path -Parent $PSScriptRoot) 'Directory.Packages.props'
    $version = "$(([xml](Get-Content -LiteralPath $packages -Raw)).SelectSingleNode("/Project/ItemGroup/PackageVersion[@Include='Velopack']/@Version").Value)"
    if (-not $version) { throw "No Velopack version in $packages" }
    $version
}

function Find-VcVars {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) {
        throw "vswhere.exe not found ($vswhere). Install Visual Studio or the Visual Studio Build Tools with the C++ x86/x64 tools."
    }
    $vs = "$(& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)".Trim()
    $vcvars = if ($vs) { Join-Path $vs 'VC\Auxiliary\Build\vcvarsall.bat' }
    if (-not $vcvars -or -not (Test-Path -LiteralPath $vcvars)) {
        throw 'No Visual Studio install with the C++ x86/x64 tools found. Add "MSVC Build Tools for x64/x86" in the Visual Studio Installer.'
    }
    $vcvars
}

# vcvarsall.bat runs vswhere.exe by name and the VS Installer folder isn't on PATH by default (cmd moans on every
# build). PATH goes back in the finally
function Invoke-VcVars([string]$VcVars, [string]$Command) {
    $installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
    $oldPath = $env:PATH
    try {
        if (-not (($env:PATH -split ';') -contains $installer)) { $env:PATH = "$installer;$env:PATH" }
        cmd /c "call ""$VcVars"" x64_x86 && $Command"
    } finally {
        $env:PATH = $oldPath
    }
}
