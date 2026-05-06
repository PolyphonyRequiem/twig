<#
.SYNOPSIS
    Measures twig CLI cold-start time (time to first stdout byte).
.DESCRIPTION
    Publishes the AOT binary, then spawns it N times measuring wall-clock
    time from process start to first output byte. Reports p50, p95, p99.
.PARAMETER Iterations
    Number of times to run the binary. Default: 30.
.PARAMETER Binary
    Path to the twig binary. If not specified, uses the default publish path.
.PARAMETER Command
    Command to run. Default: "--version" (fastest path).
#>
param(
    [int]$Iterations = 30,
    [string]$Binary,
    [string]$Command = "--version"
)

$ErrorActionPreference = 'Stop'

if (-not $Binary) {
    $Binary = Join-Path $PSScriptRoot ".." "src" "Twig" "bin" "Release" "net11.0" "win-x64" "publish" "twig.exe"

    if (-not (Test-Path $Binary)) {
        Write-Host "Publishing AOT binary..." -ForegroundColor Cyan
        Push-Location (Join-Path $PSScriptRoot ".." "src" "Twig")
        try {
            dotnet publish -c Release -r win-x64 --self-contained -v q
            if ($LASTEXITCODE -ne 0) { throw "Publish failed" }
        } finally {
            Pop-Location
        }
    }
}

if (-not (Test-Path $Binary)) {
    Write-Error "Binary not found: $Binary"
    return
}

Write-Host "Binary: $Binary" -ForegroundColor Gray
Write-Host "Command: $Command" -ForegroundColor Gray
Write-Host "Iterations: $Iterations" -ForegroundColor Gray
Write-Host ""

$durations = [System.Collections.Generic.List[double]]::new($Iterations)

for ($i = 1; $i -le $Iterations; $i++) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()

    $psi = [System.Diagnostics.ProcessStartInfo]::new($Binary, $Command)
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    # Ensure twig doesn't try to find a workspace
    $psi.WorkingDirectory = $env:TEMP

    $proc = [System.Diagnostics.Process]::Start($psi)
    # Read first byte — this is our "time to first output" measurement
    $null = $proc.StandardOutput.Read()
    $sw.Stop()

    $proc.WaitForExit(5000) | Out-Null
    if (-not $proc.HasExited) { $proc.Kill() }

    $durations.Add($sw.Elapsed.TotalMilliseconds)

    if ($i % 10 -eq 0) {
        Write-Host "  $i/$Iterations complete..." -ForegroundColor DarkGray
    }
}

$sorted = $durations | Sort-Object
$p50 = $sorted[[math]::Floor($sorted.Count * 0.50)]
$p95 = $sorted[[math]::Floor($sorted.Count * 0.95)]
$p99 = $sorted[[math]::Floor($sorted.Count * 0.99)]
$min = $sorted[0]
$max = $sorted[-1]
$avg = ($sorted | Measure-Object -Average).Average

Write-Host ""
Write-Host "=== Cold Start Results (${Iterations} iterations) ===" -ForegroundColor Green
Write-Host "  Min:  $([math]::Round($min, 2)) ms"
Write-Host "  Avg:  $([math]::Round($avg, 2)) ms"
Write-Host "  p50:  $([math]::Round($p50, 2)) ms"
Write-Host "  p95:  $([math]::Round($p95, 2)) ms"
Write-Host "  p99:  $([math]::Round($p99, 2)) ms"
Write-Host "  Max:  $([math]::Round($max, 2)) ms"

# Output as JSON for CI consumption
$result = @{
    binary = $Binary
    command = $Command
    iterations = $Iterations
    min_ms = [math]::Round($min, 2)
    avg_ms = [math]::Round($avg, 2)
    p50_ms = [math]::Round($p50, 2)
    p95_ms = [math]::Round($p95, 2)
    p99_ms = [math]::Round($p99, 2)
    max_ms = [math]::Round($max, 2)
}

$jsonPath = Join-Path $PSScriptRoot "cold-start-results.json"
$result | ConvertTo-Json | Set-Content -Path $jsonPath
Write-Host ""
Write-Host "Results written to: $jsonPath" -ForegroundColor Cyan
