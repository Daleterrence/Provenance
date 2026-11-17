<#
.SYNOPSIS
    Runs the app's own update check on a release: its statement, its package and its feed entry.

.DESCRIPTION
    With a tag, downloads every file on that GitHub release (a draft too) and runs tools\ReleaseCheck on them. The
    statement (<package>.sig) has to verify with the keys built into the app at this checkout, and match the package
    and the release's own releases.<channel>.json entry. -Folder checks a release's files that are already on disk.
    -Live checks what the update server serves: each channel's feed, its newest package and that package's statement,
    fetched the way a copy of the app fetches them.
    Exits 0 only when everything passes.
.EXAMPLE
    pwsh tools/check-release.ps1 v1.0.0-rc.1
.EXAMPLE
    pwsh tools/check-release.ps1 v1.0.0-rc.1 -Folder C:\Releases\1.0.0-rc.1
.EXAMPLE
    pwsh tools/check-release.ps1 -Live
#>
#Requires -Version 7
[CmdletBinding(DefaultParameterSetName = 'Release')]
param(
    [Parameter(Mandatory, Position = 0, ParameterSetName = 'Release')]
    [Parameter(Mandatory, Position = 0, ParameterSetName = 'Folder')]
    [ValidatePattern('^v\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string]$Tag,
    [Parameter(Mandatory, ParameterSetName = 'Folder')]
    [string]$Folder,
    [Parameter(Mandatory, ParameterSetName = 'Live')]
    [switch]$Live,
    [Parameter(ParameterSetName = 'Live')]
    [string]$Server = 'https://updates.provenancexi.app/'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $PSScriptRoot 'ReleaseCheck\ReleaseCheck.csproj'
$commit = "$(& git -C $repo rev-parse --short HEAD 2>$null)".Trim()
Write-Host "Checking with the keys built into the app at $(if ($commit) { "commit $commit" } else { $repo })"

function Invoke-ReleaseCheck([string[]]$CheckArgs) {
    & dotnet run --project $project -c Release -- @CheckArgs | Write-Host
    $LASTEXITCODE
}

# a draft's files, through the API, for a gh that won't find a draft by its tag
function Save-ReleaseAssets([string]$ReleaseTag, [string]$Dir) {
    $assets = @(& gh api 'repos/{owner}/{repo}/releases' --paginate --jq ".[] | select(.tag_name == `"$ReleaseTag`") | .assets[] | [.url, .name] | @tsv")
    if ($LASTEXITCODE -ne 0 -or -not $assets) { throw "No release $ReleaseTag on GitHub, draft or published." }
    $token = "$(& gh auth token)".Trim()
    foreach ($line in $assets) {
        $url, $name = $line -split "`t", 2
        Invoke-WebRequest -Uri $url -OutFile (Join-Path $Dir $name) -Headers @{ Authorization = "Bearer $token"; Accept = 'application/octet-stream' }
    }
}

if ($Live) { exit (Invoke-ReleaseCheck @('--live', $Server)) }
$version = $Tag.Substring(1)
if ($Folder) { exit (Invoke-ReleaseCheck @((Resolve-Path -LiteralPath $Folder).Path, $version)) }

$dir = Join-Path ([IO.Path]::GetTempPath()) "provenance-check-$version-$PID"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$code = 1
try {
    # gh finds the GitHub repo from this checkout's remote
    Push-Location $repo
    try {
        & gh release download $Tag --dir $dir --clobber
        if ($LASTEXITCODE -ne 0) { Save-ReleaseAssets $Tag $dir }
    } finally {
        Pop-Location
    }
    $code = Invoke-ReleaseCheck @($dir, $version)
} finally {
    Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
}
exit $code
