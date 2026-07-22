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
$output = Join-Path $addons "arma_ai_bridge_client.pbo"
if (Test-Path $output) {
    Remove-Item -LiteralPath $output -Force
}

if (-not (Test-Path $AddonBuilderPath)) {
    if (-not (Get-Command "python" -ErrorAction SilentlyContinue)) {
        throw "Neither AddonBuilder nor Python was found; the required PBO cannot be packed."
    }
    Write-Host "AddonBuilder was not found; packing a deterministic uncompressed development PBO..."
    & python (Join-Path $PSScriptRoot "package_pbo.py") pack $source $output --prefix "arma_ai_bridge_client"
    if ($LASTEXITCODE -ne 0) {
        throw "Portable PBO packer failed with exit code $LASTEXITCODE."
    }
    return
}

Write-Host "Packing Arma addon..."
$toolsRoot = Split-Path -Parent (Split-Path -Parent $AddonBuilderPath)
$binarize = Join-Path $toolsRoot "Binarize\binarize.exe"
$cfgConvert = Join-Path $toolsRoot "CfgConvert\CfgConvert.exe"
$fileBank = Join-Path $toolsRoot "FileBank\FileBank.exe"
$dsSignFile = Join-Path $toolsRoot "DSSignFile\DSSignFile.exe"
foreach ($requiredTool in $binarize, $cfgConvert, $fileBank, $dsSignFile) {
    if (-not (Test-Path $requiredTool)) {
        throw "Required Arma Tools component was not found: '$requiredTool'."
    }
}
$fileSystem = New-Object -ComObject Scripting.FileSystemObject
$toolsShortPath = $fileSystem.GetFolder($toolsRoot).ShortPath
if ([string]::IsNullOrWhiteSpace($toolsShortPath)) {
    $toolsShortPath = $toolsRoot
}
$builderArguments = @(
    $source,
    $addons,
    "-clear",
    "-packonly",
    "-prefix=arma_ai_bridge_client",
    "-toolsDirectory=$toolsShortPath"
)
& $AddonBuilderPath $builderArguments
if ($LASTEXITCODE -ne 0) {
    throw "AddonBuilder failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path $output)) {
    throw "AddonBuilder completed without producing '$output'."
}

Write-Host "PBO output: $addons"
