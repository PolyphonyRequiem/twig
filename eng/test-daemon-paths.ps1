param([string] $OperatorDirectory = $PSScriptRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $OperatorDirectory 'daemon-launch.ps1')
$fixture = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) (
    'Temp\twig-daemon-paths-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
$junction = Join-Path $fixture 'junction'
function Assert-PathRefused([string] $Path) {
    $refused = $false
    try { Get-BenchLocalPath $Path | Out-Null } catch { $refused = $true }
    if (-not $refused) { throw 'An unsafe or unreadable path was accepted.' }
}
try {
    $leaf = Join-Path $fixture 'file'
    [IO.File]::WriteAllText($leaf, 'fixture')
    foreach ($path in @($fixture, $leaf, (Join-Path $fixture 'missing\leaf'),
        (Join-Path $junction 'missing\leaf'))) {
        if ((Get-BenchLocalPath $path) -cne $path) { throw 'Valid local path changed.' }
    }
    foreach ($path in @('', 'relative', '\\server\share\file', ($fixture + ' '),
        ($leaf + ':stream'), ($fixture + '\*'), ([IO.Path]::GetPathRoot($fixture)))) {
        Assert-PathRefused $path
    }
    $longComponent = Join-Path $fixture ('x' * 256)
    $attributeReadFailed = $false
    try { [IO.File]::GetAttributes($longComponent) | Out-Null }
    catch [IO.IOException] {
        if ($_.Exception.InnerException -is [IO.FileNotFoundException] -or
            $_.Exception.InnerException -is [IO.DirectoryNotFoundException]) { throw }
        $attributeReadFailed = $true
    }
    if (-not $attributeReadFailed) { throw 'Expected a real non-missing filesystem attribute failure.' }
    Assert-PathRefused $longComponent

    $target = Join-Path $fixture 'target'
    [void][IO.Directory]::CreateDirectory($target)
    [IO.File]::WriteAllText((Join-Path $target 'file'), 'fixture')
    New-Item -ItemType Junction -Path $junction -Target $target | Out-Null
    if (-not ([IO.File]::GetAttributes($junction) -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Expected a real reparse-point fixture.'
    }
    Assert-PathRefused $junction
    Assert-PathRefused (Join-Path $junction 'file')
    Assert-PathRefused (Join-Path $junction 'missing\leaf')
    Write-Output 'PASS daemon local paths: existing/missing leaves, malformed inputs, metadata-read failure and reparse ancestors.'
} finally {
    if ([IO.Directory]::Exists($junction)) { [IO.Directory]::Delete($junction) }
    [IO.Directory]::Delete($fixture, $true)
}
