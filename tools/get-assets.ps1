<#
.SYNOPSIS
    Downloads Provenance's images into this checkout's assets folder.
.DESCRIPTION
    The images aren't in the repository. tools\assets.json names the zip this commit builds with and its SHA-256. This
    downloads that zip, checks it against the hash and unpacks it into assets\. It does nothing when the right ones are
    already there, and native\build-native.ps1 and tools\pack-release.ps1 run it for you when there are no images.
    A folder set in PROVENANCE_ASSETS is used instead of the downloaded one.
.EXAMPLE
    pwsh tools/get-assets.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
# Windows PowerShell draws a progress bar per chunk, which makes the download crawl
$ProgressPreference = 'SilentlyContinue'
$pin = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'assets.json') -Raw | ConvertFrom-Json
$dir = Join-Path (Split-Path -Parent $PSScriptRoot) 'assets'
$mark = Join-Path $dir '.fetched'
$icon = Join-Path $dir 'app\provenance.ico'

function Test-Fetched {
    (Test-Path -LiteralPath $mark) -and (Test-Path -LiteralPath $icon) -and "$(Get-Content -LiteralPath $mark -Raw)".Trim() -ceq $pin.sha256
}

if (Test-Fetched) { return }
if ((Test-Path -LiteralPath $dir) -and -not (Test-Path -LiteralPath $mark)) {
    if (Test-Path -LiteralPath $icon) {
        Write-Host "$dir wasn't downloaded by this script, so it's been left as it is."
        return
    }
    throw "$dir wasn't downloaded by this script and has no app\provenance.ico in it. Move it out of the way and run this again."
}

$run = [guid]::NewGuid().ToString('N')
$zip = Join-Path ([IO.Path]::GetTempPath()) "provenance-assets-$run.zip"
$fresh = "$dir.$run"
try {
    Write-Host "Downloading Provenance's images from $($pin.url)"
    Invoke-WebRequest -Uri $pin.url -OutFile $zip -UseBasicParsing
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -cne $pin.sha256) {
        throw "The download isn't the file tools\assets.json names (its SHA-256 is $hash), so nothing was unpacked."
    }
    Expand-Archive -LiteralPath $zip -DestinationPath $fresh
    Set-Content -LiteralPath (Join-Path $fresh '.fetched') -Value $pin.sha256 -Encoding ascii
    # two builds can get here together, and the second has nothing left to do
    if (Test-Fetched) { return }
    try {
        if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
        Rename-Item -LiteralPath $fresh -NewName (Split-Path -Leaf $dir)
    } catch {
        if (-not (Test-Fetched)) { throw }
    }
    Write-Host "Provenance's images are in $dir"
} finally {
    Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $fresh) { Remove-Item -LiteralPath $fresh -Recurse -Force -ErrorAction SilentlyContinue }
}
