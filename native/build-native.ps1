[CmdletBinding()]
param(
    [string]$OutDir = (Join-Path $PSScriptRoot 'bin')
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\tools\build-common.ps1')

$vcvars = Find-VcVars
$assets = Join-Path (Find-ProvenanceAssets) 'app'

$OutDir = [IO.Path]::GetFullPath($OutDir)
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$version = Get-ProvenanceVersion
$product = $version.Version
$commit = if (Get-Command git -ErrorAction SilentlyContinue) { "$(git -C $PSScriptRoot rev-parse --short=7 HEAD 2>$null)".Trim() }
if ($commit -match '^[0-9a-f]{7}$') { $product += "+$commit" }
@(
    "#define NATIVE_FILEVERSION $($version.Prefix.Replace('.', ',')),0"
    "#define NATIVE_FILEVERSION_STR ""$($version.Prefix).0"""
    "#define NATIVE_PRODUCTVERSION_STR ""$product"""
) | Set-Content -LiteralPath (Join-Path $OutDir 'native_version.h') -Encoding ascii

function Source([string]$name) { '"' + (Join-Path $PSScriptRoot $name) + '"' }

$clHook = "cl /nologo /O2 /W4 /WX /MT /I $(Source '.') $(Source 'hook\pol_hook.c') /LD /Fepol_hook.dll /link $HookLibs"
$clWatcher = "rc /nologo /i ""$OutDir"" /i ""$assets"" /fo watcher.res $(Source 'watcher.rc') && " +
    "cl /nologo /O2 /W4 /WX /MT $(Source 'watcher.c') watcher.res /Fe""Provenance Watcher.exe"" /link user32.lib advapi32.lib"
function UninstallCl([string]$exe, [string]$tag, [string]$rcDefine, [string]$clDefine) {
    "rc /nologo $rcDefine /i ""$OutDir"" /i ""$assets"" /fo uninstall_$tag.res $(Source 'uninstall.rc') && " +
    "cl /nologo /O2 /W4 /WX /MT $clDefine $(Source 'uninstall.c') uninstall_$tag.res /Founinstall_$tag.obj /Fe""$exe""" +
    " /link $UninstallLibs /SUBSYSTEM:WINDOWS /MANIFEST:EMBED" +
    " /MANIFESTUAC:""level='requireAdministrator' uiAccess='false'"""
}
$polUninstall = 'Provenance - PlayOnline Uninstall.exe'
$ffxiUninstall = 'Provenance - FINAL FANTASY XI Uninstall.exe'

Push-Location -LiteralPath $OutDir
try {
    Invoke-NativeBuild $vcvars $clHook (Join-Path $OutDir 'pol_hook.dll')
    Invoke-NativeBuild $vcvars $clWatcher (Join-Path $OutDir 'Provenance Watcher.exe')
    Invoke-NativeBuild $vcvars (UninstallCl $polUninstall 'pol' '' '') (Join-Path $OutDir $polUninstall)
    Invoke-NativeBuild $vcvars (UninstallCl $ffxiUninstall 'ffxi' '/d FFXI_UNINSTALL' '/DFFXI_UNINSTALL') (Join-Path $OutDir $ffxiUninstall)
} finally {
    Pop-Location
}
