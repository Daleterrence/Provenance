<#
.SYNOPSIS
    Builds the native files Provenance ships: the hook it loads into pol.exe (pol_hook.dll), Provenance Watcher.exe
    (starts pol.exe and puts the hook in), and the uninstallers for the PlayOnline and FINAL FANTASY XI installs it
    applies itself.

.DESCRIPTION
    Builds them from the sources next to this script into native\bin, or into -OutDir. Needs the x86 C++ compiler from
    Visual Studio or the Visual Studio Build Tools (found with vswhere) and stops on the first compiler error.

.EXAMPLE
    pwsh native/build-native.ps1
#>
[CmdletBinding()]
param(
    [string]$OutDir = (Join-Path $PSScriptRoot 'bin')
)

$ErrorActionPreference = 'Stop'

$installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
$vswhere = Join-Path $installer 'vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw "vswhere.exe not found ($vswhere). Install Visual Studio or the Visual Studio Build Tools with the C++ x86/x64 tools."
}
$vs = "$(& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)".Trim()
$vcvars = if ($vs) { Join-Path $vs 'VC\Auxiliary\Build\vcvarsall.bat' }
if (-not $vcvars -or -not (Test-Path -LiteralPath $vcvars)) {
    throw 'No Visual Studio install with the C++ x86/x64 tools found. Add "MSVC Build Tools for x64/x86" in the Visual Studio Installer.'
}

$OutDir = [IO.Path]::GetFullPath($OutDir)
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# the exes carry the same version as Provenance.exe, Windows wants four numbers in the file version
$props = [xml](Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\src\Directory.Build.props'))
$prefix = "$($props.SelectSingleNode('/Project/PropertyGroup/VersionPrefix').InnerText)".Trim()
$suffix = "$($props.SelectSingleNode('/Project/PropertyGroup/VersionSuffix').InnerText)".Trim()
if ($prefix -notmatch '^\d+\.\d+\.\d+$') { throw "src\Directory.Build.props has no usable VersionPrefix ($prefix)" }
$product = if ($suffix) { "$prefix-$suffix" } else { $prefix }
$commit = if (Get-Command git -ErrorAction SilentlyContinue) { "$(git -C $PSScriptRoot rev-parse --short=7 HEAD 2>$null)".Trim() }
if ($commit -match '^[0-9a-f]{7}$') { $product += "+$commit" }
@(
    "#define NATIVE_FILEVERSION $($prefix.Replace('.', ',')),0"
    "#define NATIVE_FILEVERSION_STR ""$prefix.0"""
    "#define NATIVE_PRODUCTVERSION_STR ""$product"""
) | Set-Content -LiteralPath (Join-Path $OutDir 'native_version.h') -Encoding ascii

function Invoke-Cl([string]$cl, [string]$out) {
    cmd /c "call ""$vcvars"" x64_x86 && $cl"
    if ($LASTEXITCODE -ne 0) {
        throw "native build failed: $out (exit code $LASTEXITCODE)"
    }
    Write-Host "built $(Join-Path $OutDir $out)"
}

function Source([string]$name) { '"' + (Join-Path $PSScriptRoot $name) + '"' }

# ole32 for CoTaskMemFree (pol.exe imports it anyway), uuid for FOLDERID_ProgramData. shell32 gets looked
# up at run time, so the hook adds no DLL to pol.exe's start-up.
$hookLibs = 'user32.lib advapi32.lib crypt32.lib ole32.lib uuid.lib'
$clHook = "cl /nologo /O2 /W3 $(Source 'pol_hook.c') /LD /Fepol_hook.dll /link $hookLibs"
# the exes all get Provenance's own icon
$assets = Join-Path $PSScriptRoot '..\src\Provenance.Wpf\Assets'
$clWatcher = "rc /nologo /i ""$OutDir"" /i ""$assets"" /fo watcher.res $(Source 'watcher.rc') && " +
    "cl /nologo /O2 /W3 $(Source 'watcher.c') watcher.res /Fe""Provenance Watcher.exe"" /link user32.lib advapi32.lib"
# the uninstallers delete from Program Files and HKLM, so they always ask for admin. One build per product, so the
# UAC prompt names the one it's removing
function UninstallCl([string]$exe, [string]$tag, [string]$rcDefine, [string]$clDefine) {
    "rc /nologo $rcDefine /i ""$OutDir"" /i ""$assets"" /fo uninstall_$tag.res $(Source 'uninstall.rc') && " +
    "cl /nologo /O2 /W3 /MT $clDefine $(Source 'uninstall.c') uninstall_$tag.res /Founinstall_$tag.obj /Fe""$exe""" +
    ' /link user32.lib advapi32.lib ole32.lib oleaut32.lib shell32.lib /SUBSYSTEM:WINDOWS /MANIFEST:EMBED' +
    " /MANIFESTUAC:""level='requireAdministrator' uiAccess='false'"""
}
$polUninstall = 'Provenance - PlayOnline Uninstall.exe'
$ffxiUninstall = 'Provenance - FINAL FANTASY XI Uninstall.exe'

# vcvarsall.bat runs vswhere.exe by name and the VS Installer folder isn't on PATH by default (cmd moans on
# every build). PATH goes back in the finally.
$oldPath = $env:PATH
Push-Location -LiteralPath $OutDir
try {
    if (-not (($env:PATH -split ';') -contains $installer)) {
        $env:PATH = "$installer;$env:PATH"
    }
    Invoke-Cl $clHook 'pol_hook.dll'
    Invoke-Cl $clWatcher 'Provenance Watcher.exe'
    Invoke-Cl (UninstallCl $polUninstall 'pol' '' '') $polUninstall
    Invoke-Cl (UninstallCl $ffxiUninstall 'ffxi' '/d FFXI_UNINSTALL' '/DFFXI_UNINSTALL') $ffxiUninstall
} finally {
    $env:PATH = $oldPath
    Pop-Location
}
