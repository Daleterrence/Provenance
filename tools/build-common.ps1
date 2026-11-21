$HookLibs = 'user32.lib advapi32.lib crypt32.lib bcrypt.lib'
$UninstallLibs = 'user32.lib advapi32.lib ole32.lib oleaut32.lib shell32.lib ntdll.lib'

$BuildValues = @{}
function Get-BuildValues {
    if ($BuildValues.Count) { return $BuildValues.Common }
    $project = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\Provenance.Common\Provenance.Common.csproj'
    $out = [IO.Path]::GetTempFileName()
    try {
        $ask = '-getProperty:Version', '-getProperty:ProvenanceAssetsDir', '-getItem:PackageVersion'
        $null = & dotnet msbuild $project -nologo @ask "-getResultOutputFile:$out"
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
    $own = Join-Path (Split-Path -Parent $PSScriptRoot) 'assets'
    if (-not $found -or $found.TrimEnd('\') -eq $own) {
        & (Join-Path $PSScriptRoot 'get-assets.ps1')
        $BuildValues.Clear()
        $found = (Get-BuildValues).Properties.ProvenanceAssetsDir
    }
    if (-not $found -or -not (Test-Path -LiteralPath (Join-Path $found 'app\provenance.ico'))) {
        throw "Provenance's images aren't here. Run tools\get-assets.ps1 to download them, or set PROVENANCE_ASSETS to a folder that has them."
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

# it runs vswhere.exe by name, and the VS Installer folder isn't on PATH by default
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

function Invoke-NativeBuild([string]$VcVars, [string]$Command, [string]$Out) {
    Invoke-VcVars $VcVars $Command
    if ($LASTEXITCODE -ne 0) { throw "native build failed: $Out (exit code $LASTEXITCODE)" }
    Write-Host "built $Out"
}
