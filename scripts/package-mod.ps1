[CmdletBinding()]
param(
    [string]$AddonBuilderPath = "${env:ProgramFiles(x86)}\Steam\steamapps\common\Arma 3 Tools\AddonBuilder\AddonBuilder.exe",
    [string]$DestinationModPath
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root "arma3\addon-source\arma_ai_bridge_client"

if (-not $DestinationModPath) {
    $DestinationModPath = Join-Path $root "artifacts\mod\@Arma_AI_Bridge"
}

$addons = Join-Path $DestinationModPath "addons"
New-Item -ItemType Directory -Force -Path $addons | Out-Null

if (-not (Test-Path $AddonBuilderPath)) {
    Write-Warning "AddonBuilder was not found at '$AddonBuilderPath'. The app and DLL are built, but the PBO was not packed."
    return
}

Write-Host "Packing Arma addon..."
& $AddonBuilderPath $source $addons -clear -packonly
if ($LASTEXITCODE -ne 0) {
    throw "AddonBuilder failed with exit code $LASTEXITCODE."
}

Write-Host "PBO output: $addons"
