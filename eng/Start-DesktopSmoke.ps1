#requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PackageRoot,
    [Parameter(Mandatory)][string] $CanvasRoot,
    [Parameter(Mandatory)][string] $CanvasCheckout,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string] $Certificate,
    [switch] $PreflightOnly
)
$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $PackageRoot).Path
$canvas = (Resolve-Path -LiteralPath $CanvasRoot).Path
$checkout = (Resolve-Path -LiteralPath $CanvasCheckout).Path
$cli = (Resolve-Path -LiteralPath (Join-Path $package 'unpacked-cli\twig.exe')).Path
$check = (Resolve-Path -LiteralPath (Join-Path $checkout 'tools\desktop_smoke.py')).Path
$stage = (Resolve-Path -LiteralPath (Join-Path $checkout 'tools\stage-developer-panel.ps1')).Path
$project = (Resolve-Path "$PSScriptRoot\..\tests\Twig.Cli.Tests\Twig.Cli.Tests.csproj").Path
$python = $null
$originalPath = $env:Path
try {
    # A desktop terminal can inherit an older PATH than the user's current Windows settings.
    $env:Path = (@($originalPath, [Environment]::GetEnvironmentVariable('Path','Machine'),
        [Environment]::GetEnvironmentVariable('Path','User')) -join ';')
    foreach ($name in 'py','python3','python') {
        $command = Get-Command $name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if (!$command) { continue }
        $prefix = @()
        if ($name -eq 'py') { $prefix = @('-3') }
        & $command.Source @prefix -I -c 'import sqlite3,sys; sys.exit(sys.version_info.major != 3)' 2>$null
        if ($LASTEXITCODE -eq 0) { $python = $command.Source; break }
    }
} finally { $env:Path = $originalPath }
if (!$python) { throw 'Desktop smoke needs Python 3 with sqlite3 (py -3, python3, or python). No fixture was started.' }
$handoff = Join-Path $package ("twig-desktop-" + [guid]::NewGuid() + '.txt')
$start = [Diagnostics.ProcessStartInfo]::new('dotnet')
$start.UseShellExecute = $false
foreach ($arg in @('test', $project, '--no-build', '--tl:off', '--filter', 'FullyQualifiedName~NativeDesktopOwnerAdmitsOnlyTheSelectedLocalWorkspace')) {
    $start.ArgumentList.Add($arg)
}
if (!$PreflightOnly) {
    # Give the fixture's 20-minute operator window time to expire and tear down normally.
    $start.ArgumentList.Add('--')
    $start.ArgumentList.Add('RunConfiguration.TestSessionTimeout=1500000')
}
$start.Environment['TWIG_TEST_OWNER_CERT'] = $Certificate
$start.Environment['TWIG_TEST_PACKAGE_ROOT'] = $package
$start.Environment['TWIG_DESKTOP_HANDOFF'] = $handoff
$previous = @{}
foreach ($key in 'TWIG_OWNER_FIXTURE_ID','TWIG_USER_HOME','TWIG_SERVER_INSTALL_ROOT') {
    $previous[$key] = [Environment]::GetEnvironmentVariable($key)
}
$root = $null
$pushed = $false
$runner = [Diagnostics.Process]::Start($start)
function Read-DesktopStep([string] $Prompt) {
    Write-Host $Prompt
    if ([Console]::IsInputRedirected) {
        $answer = [Console]::ReadLine()
        if ($null -eq $answer) { throw 'Desktop input closed; stopping the fixture.' }
    } else {
        while (![Console]::KeyAvailable) {
            if ($runner.HasExited) { throw 'Desktop fixture ended. Do not use its plugin folder.' }
            Start-Sleep -Milliseconds 200
        }
        $answer = Read-Host
    }
    if ($runner.HasExited) { throw 'Desktop fixture ended. Do not use its plugin folder.' }
    return $answer
}
try {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while (!(Test-Path -LiteralPath $handoff)) {
        if ($runner.HasExited -or $clock.Elapsed.TotalSeconds -gt 75) { throw 'Native fixture did not become ready.' }
        Start-Sleep -Milliseconds 300
    }
    $candidate = [IO.File]::ReadAllText($handoff).Trim()
    $id = [guid]::ParseExact((Split-Path $candidate -Leaf), 'D')
    $expected = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "Twig.OwnerFixtures\$id"
    if ($candidate -cne $expected) { throw 'Unexpected fixture handoff location.' }
    $root = $expected
    $env:TWIG_OWNER_FIXTURE_ID = $id.ToString('D')
    $env:TWIG_USER_HOME = Join-Path $root 'home'
    $env:TWIG_SERVER_INSTALL_ROOT = $root
    Push-Location -LiteralPath (Join-Path $root 'work')
    $pushed = $true
    $record = & $cli server endpoint -o json
    if ($LASTEXITCODE) { throw 'Matched CLI could not inspect its selected endpoint.' }
    $record = $record | ConvertFrom-Json
    if ($record.serverId -ne $id -or $record.certificateThumbprint -ne $Certificate) { throw 'Wrong fixture endpoint.' }
    $boot = [IO.File]::ReadAllLines((Join-Path $env:TWIG_USER_HOME 'owner.run'))[0]
    $plugin = & $stage -FixtureId $id -Boot $boot -Origin $record.origin -Thumbprint $Certificate `
        -SelectionId $record.selectionId -Cli $cli -CanvasRoot $canvas
    & $python @prefix -I -B $check
    if ($LASTEXITCODE) { throw 'The native fixture is not ready.' }
    if (!$PreflightOnly) {
        Write-Host "Register this local developer plugin in Copilot (20-minute session):`n$plugin"
        $null = Read-DesktopStep 'Open Twig (native developer) in the picker, then press Enter here'
        & $cli workspace --bench 'Unrelated pin' --refresh -o json | Out-Null
        if ($LASTEXITCODE) { throw 'Cannot refresh the pinned native test item.' }
        Write-Host 'In Unrelated pin, edit the title of item 1 in Field value and click Save once.'
        Write-Host 'If confirmation is lost, check the connection and use Check original save. Do not click Save again.'
        $original = [guid]::ParseExact((Read-DesktopStep 'Enter the UUID from Saved: <UUID>. If the panel reports failure or cannot confirm the original, stop with Ctrl+C.'), 'D')
        $null = Read-DesktopStep 'Reopen the item and confirm its provider title matches the saved edit, then press Enter (do not save again)'
        $clock.Restart()
        do {
            $found = & $cli update --find --id 1
            if ($LASTEXITCODE -eq 0 -and ($found | ConvertFrom-Json).matches[0].state -eq 'Complete') { break }
            Start-Sleep -Milliseconds 300
        } while ($clock.Elapsed.TotalSeconds -lt 15)
        $found | & $python @prefix -I -B $check --submission $original.ToString('D')
        if ($LASTEXITCODE) { throw 'The original single effect was not verified; do not retry publication.' }
    }
} finally {
    if ($pushed) { Pop-Location }
    foreach ($key in $previous.Keys) { [Environment]::SetEnvironmentVariable($key, $previous[$key]) }
    if ($root -and (Test-Path -LiteralPath $root)) { [IO.File]::WriteAllText((Join-Path $root 'desktop.finished'), '') }
    if (!$runner.WaitForExit(20000)) { $runner.Kill($true); $runner.WaitForExit() }
    $exitCode = $runner.ExitCode
    $runner.Dispose()
    if (Test-Path -LiteralPath $handoff) { Remove-Item -LiteralPath $handoff }
}
if ($exitCode) { throw "Fixture runner exited with code $exitCode." }
