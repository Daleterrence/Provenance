<#
.SYNOPSIS
    Builds the native files Provenance ships: the hook it loads into pol.exe (pol_hook.dll), pol_launch.exe and
    pol_uninstall.exe.

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
# shell32 is only for --windower's wide arguments, delay-loaded so the other launches never load it
$clLaunch = "cl /nologo /O2 /W3 $(Source 'pol_launch.c') /Fepol_launch.exe /link user32.lib advapi32.lib shell32.lib" +
    ' delayimp.lib /DELAYLOAD:shell32.dll'
# the uninstaller deletes from Program Files and HKLM, so it always asks for admin
$clUninstall = "cl /nologo /O2 /W3 /MT $(Source 'pol_uninstall.c') /Fepol_uninstall.exe /link user32.lib advapi32.lib" +
    ' ole32.lib oleaut32.lib shell32.lib /SUBSYSTEM:WINDOWS /MANIFEST:EMBED' +
    " /MANIFESTUAC:""level='requireAdministrator' uiAccess='false'"""

# vcvarsall.bat runs vswhere.exe by name and the VS Installer folder isn't on PATH by default (cmd moans on
# every build). PATH goes back in the finally.
$oldPath = $env:PATH
Push-Location -LiteralPath $OutDir
try {
    if (-not (($env:PATH -split ';') -contains $installer)) {
        $env:PATH = "$installer;$env:PATH"
    }
    Invoke-Cl $clHook 'pol_hook.dll'
    Invoke-Cl $clLaunch 'pol_launch.exe'
    Invoke-Cl $clUninstall 'pol_uninstall.exe'
} finally {
    $env:PATH = $oldPath
    Pop-Location
}
