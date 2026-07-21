[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [switch]$SkipPbo
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
$appOut = Join-Path $artifacts "app"
$modOut = Join-Path $artifacts "mod\@Arma_AI_Bridge"
$nativeBuild = Join-Path $root "native\ArmaAiBridge\build"

function Require-Command([string]$Name) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' was not found."
    }
}

Require-Command "dotnet"
Require-Command "cmake"

New-Item -ItemType Directory -Force -Path $appOut, $modOut, (Join-Path $modOut "addons") | Out-Null

Write-Host "Building ArmA AI Bridge WPF application..."
dotnet publish (Join-Path $root "src\ArmaAiBridge.App\ArmaAiBridge.App.csproj") `
    -c $Configuration `
    -r win-x64 `
    --self-contained false `
    -o $appOut

Write-Host "Building Arma x64 extension..."
cmake -S (Join-Path $root "native\ArmaAiBridge") -B $nativeBuild -A x64
cmake --build $nativeBuild --config $Configuration

$extension = Get-ChildItem -Path $nativeBuild -Recurse -Filter "arma_ai_bridge_x64.dll" | Select-Object -First 1
if (-not $extension) { throw "Native extension output was not found." }
Copy-Item $extension.FullName (Join-Path $modOut "arma_ai_bridge_x64.dll") -Force
Copy-Item (Join-Path $root "arma3\@Arma_AI_Bridge\mod.cpp") (Join-Path $modOut "mod.cpp") -Force

if (-not $SkipPbo) {
    & (Join-Path $PSScriptRoot "package-mod.ps1") -DestinationModPath $modOut
}

Write-Host "Build completed: $artifacts"
