[CmdletBinding()]
param(
    [ValidateSet("private", "public")]
    [string]$Visibility = "private"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI is not installed. Install it, then run 'gh auth login'."
}

Push-Location $root
try {
    gh auth status
    if (-not (Test-Path ".git")) {
        git init -b main
        git add .
        git commit -m "Initial ArmA AI Bridge foundation"
    }

    $visibilityFlag = "--$Visibility"
    gh repo create "ViperSix11/ArmA-AI-Bridge" $visibilityFlag --source . --remote origin --push
}
finally {
    Pop-Location
}
