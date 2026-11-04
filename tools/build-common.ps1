<#
.SYNOPSIS
    The version, the assets folder, Velopack's version and the C++ tools, looked up the same way for every build script
    and CI, and the libraries the natives link. Dot-source it, it only defines functions and those two lists.
#>

# ole32 for CoTaskMemFree (pol.exe imports it anyway), uuid for FOLDERID_ProgramData. shell32 gets looked up at run
# time, so the hook adds no DLL to pol.exe's start-up
$HookLibs = 'user32.lib advapi32.lib crypt32.lib ole32.lib uuid.lib bcrypt.lib'
$UninstallLibs = 'user32.lib advapi32.lib ole32.lib oleaut32.lib shell32.lib ntdll.lib'

# the version, the assets folder and the package versions as the build sees them, asked once a script. Through a file, so
# nothing else dotnet prints gets in
$BuildValues = @{}
function Get-BuildValues {
    if ($BuildValues.Count) { return $BuildValues.Common }
    $project = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\Provenance.Common\Provenance.Common.csproj'
    $out = [IO.Path]::GetTempFileName()
    try {
        $ask = '-getProperty:Version', '-getProperty:ProvenanceAssetsDir', '-getItem:PackageVersion'
        $null = & dotnet msbuild $project -nologo @ask "-getResultOutputFile:$out"
        # with a result file MSBuild keeps its errors to itself, so a failure asks again without one to show them
        if ($LASTEXITCODE -ne 0) { throw "dotnet msbuild couldn't read ${project}:`n$(& dotnet msbuild $project -nologo @ask 2>&1 | Out-String)" }
        $BuildValues.Common = Get-Content -LiteralPath $out -Raw | ConvertFrom-Json
        $BuildValues.Common
    } finally {
        Remove-Item -LiteralPath $out -Force -ErrorAction SilentlyContinue
    }
}

function Get-ProvenanceVersion {
    $version = (Get-BuildValues).Properties.Version
    $prefix, $suffix = $version -split '-', 2
    if ($prefix -notmatch '^\d+\.\d+\.\d+$') { throw "Directory.Build.props gives a version the exes can't carry ($version)" }
    [pscustomobject]@{ Prefix = $prefix; Suffix = "$suffix"; Version = $version }
}

function Find-ProvenanceAssets {
    $found = (Get-BuildValues).Properties.ProvenanceAssetsDir
    if (-not $found -or -not (Test-Path -LiteralPath (Join-Path $found 'app\provenance.ico'))) {
        throw "Provenance's images aren't in this repo. Put the assets folder next to the repo, or set PROVENANCE_ASSETS to where it is."
    }
    $found
}

function Get-VelopackVersion {
    $version = "$((Get-BuildValues).Items.PackageVersion | Where-Object Identity -eq 'Velopack' | ForEach-Object Version)"
    if (-not $version) { throw 'No Velopack version in Directory.Packages.props' }
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

# vcvarsall takes a couple of seconds, so its environment's kept for every command after. It runs vswhere.exe by name,
# and the VS Installer folder isn't on PATH by default
$VcVarsEnvironments = @{}
function Invoke-VcVars([string]$VcVars, [string]$Command) {
    if (-not $VcVarsEnvironments.ContainsKey($VcVars)) {
        $installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
        $vars = @{}
        foreach ($line in cmd /c "set ""PATH=$installer;%PATH%"" && call ""$VcVars"" x64_x86 >nul && set") {
            if ($line -match '^([^=]+)=(.*)$') { $vars[$Matches[1]] = $Matches[2] }
        }
        if ($LASTEXITCODE -ne 0 -or -not $vars['INCLUDE']) { throw "vcvarsall.bat x64_x86 didn't set up the C++ tools ($VcVars)" }
        $VcVarsEnvironments[$VcVars] = $vars
    }
    # the script's own environment goes back afterwards
    $vars = $VcVarsEnvironments[$VcVars]
    $saved = @{}
    try {
        foreach ($name in $vars.Keys) {
            $saved[$name] = [Environment]::GetEnvironmentVariable($name)
            Set-Item -LiteralPath "Env:\$name" -Value $vars[$name]
        }
        cmd /c $Command
    } finally {
        foreach ($name in $saved.Keys) {
            if ($null -eq $saved[$name]) { Remove-Item -LiteralPath "Env:\$name" -ErrorAction SilentlyContinue }
            else { Set-Item -LiteralPath "Env:\$name" -Value $saved[$name] }
        }
    }
}

# one compile or link that stops the script when it fails
function Invoke-NativeBuild([string]$VcVars, [string]$Command, [string]$Out) {
    Invoke-VcVars $VcVars $Command
    if ($LASTEXITCODE -ne 0) { throw "native build failed: $Out (exit code $LASTEXITCODE)" }
    Write-Host "built $Out"
}
