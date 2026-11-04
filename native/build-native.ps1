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
. (Join-Path $PSScriptRoot '..\tools\build-common.ps1')

$vcvars = Find-VcVars
# the exes all get Provenance's own icon
$assets = Join-Path (Find-ProvenanceAssets) 'app'

$OutDir = [IO.Path]::GetFullPath($OutDir)
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# the exes carry the same version as Provenance.exe, Windows wants four numbers in the file version
$version = Get-ProvenanceVersion
$product = $version.Version
$commit = if (Get-Command git -ErrorAction SilentlyContinue) { "$(git -C $PSScriptRoot rev-parse --short=7 HEAD 2>$null)".Trim() }
if ($commit -match '^[0-9a-f]{7}$') { $product += "+$commit" }
@(
    "#define NATIVE_FILEVERSION $($version.Prefix.Replace('.', ',')),0"
    "#define NATIVE_FILEVERSION_STR ""$($version.Prefix).0"""
    "#define NATIVE_PRODUCTVERSION_STR ""$product"""
) | Set-Content -LiteralPath (Join-Path $OutDir 'native_version.h') -Encoding ascii

function Invoke-Cl([string]$cl, [string]$out) {
    Invoke-VcVars $vcvars $cl
    if ($LASTEXITCODE -ne 0) {
        throw "native build failed: $out (exit code $LASTEXITCODE)"
    }
    Write-Host "built $(Join-Path $OutDir $out)"
}

function Source([string]$name) { '"' + (Join-Path $PSScriptRoot $name) + '"' }

# ole32 for CoTaskMemFree (pol.exe imports it anyway), uuid for FOLDERID_ProgramData. shell32 gets looked
# up at run time, so the hook adds no DLL to pol.exe's start-up.
$hookLibs = 'user32.lib advapi32.lib crypt32.lib ole32.lib uuid.lib bcrypt.lib'
$clHook = "cl /nologo /O2 /W3 /MT $(Source 'pol_hook.c') /LD /Fepol_hook.dll /link $hookLibs"
$clWatcher = "rc /nologo /i ""$OutDir"" /i ""$assets"" /fo watcher.res $(Source 'watcher.rc') && " +
    "cl /nologo /O2 /W3 /MT $(Source 'watcher.c') watcher.res /Fe""Provenance Watcher.exe"" /link user32.lib advapi32.lib"
# the uninstallers delete from Program Files and HKLM, so they always ask for admin. One build per product, so the
# UAC prompt names the one it's removing
function UninstallCl([string]$exe, [string]$tag, [string]$rcDefine, [string]$clDefine) {
    "rc /nologo $rcDefine /i ""$OutDir"" /i ""$assets"" /fo uninstall_$tag.res $(Source 'uninstall.rc') && " +
    "cl /nologo /O2 /W3 /MT $clDefine $(Source 'uninstall.c') uninstall_$tag.res /Founinstall_$tag.obj /Fe""$exe""" +
    ' /link user32.lib advapi32.lib ole32.lib oleaut32.lib shell32.lib ntdll.lib /SUBSYSTEM:WINDOWS /MANIFEST:EMBED' +
    " /MANIFESTUAC:""level='requireAdministrator' uiAccess='false'"""
}
$polUninstall = 'Provenance - PlayOnline Uninstall.exe'
$ffxiUninstall = 'Provenance - FINAL FANTASY XI Uninstall.exe'

Push-Location -LiteralPath $OutDir
try {
    Invoke-Cl $clHook 'pol_hook.dll'
    Invoke-Cl $clWatcher 'Provenance Watcher.exe'
    Invoke-Cl (UninstallCl $polUninstall 'pol' '' '') $polUninstall
    Invoke-Cl (UninstallCl $ffxiUninstall 'ffxi' '/d FFXI_UNINSTALL' '/DFFXI_UNINSTALL') $ffxiUninstall
} finally {
    Pop-Location
}
