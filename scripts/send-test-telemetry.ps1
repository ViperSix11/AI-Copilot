$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$payload = Get-Content (Join-Path $root "samples\telemetry-v1.json") -Raw

$pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
    ".",
    "AICopilot.Arma3.Telemetry",
    [System.IO.Pipes.PipeDirection]::Out)

$writer = $null
try {
    $pipe.Connect(3000)
    $writer = [System.IO.StreamWriter]::new($pipe, [System.Text.UTF8Encoding]::new($false))
    $writer.AutoFlush = $true
    $writer.WriteLine(($payload | ConvertFrom-Json | ConvertTo-Json -Depth 20 -Compress))
    Write-Host "Test telemetry sent."
}
finally {
    if ($writer) { $writer.Dispose() }
    $pipe.Dispose()
}
