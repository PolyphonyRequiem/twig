[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Archive,
    [Parameter(Mandatory = $true)][string] $CliArchive,
    [string] $PythonExecutable = 'python'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$Archive = (Resolve-Path -LiteralPath $Archive).Path
$CliArchive = (Resolve-Path -LiteralPath $CliArchive).Path
function Assert-ArchiveChecksum([string] $Path) {
    $name = [IO.Path]::GetFileName($Path)
    $checksum = [IO.File]::ReadAllText($Path + '.sha256')
    if ($checksum -cnotmatch ('\A([a-f0-9]{64})  ' + [regex]::Escape($name) + '\r?\n\z') -or
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ine $Matches[1]) {
        throw 'The archive checksum or checksum filename is invalid.'
    }
}
Assert-ArchiveChecksum $Archive
Assert-ArchiveChecksum $CliArchive
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($Archive)
try {
    $tools = @('daemon-launch.ps1', 'daemon-install.ps1', 'daemon-run-owned.py')
    $seen = @{}
    foreach ($entry in $zip.Entries) {
        $path = $entry.FullName.Replace('\', '/')
        if ($seen.ContainsKey($path) -or $path -notmatch '\A[A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+)*/?\z' -or
            @($path.Split('/') | Where-Object { $_ -in @('.', '..') }).Count -or
            ($path -notin $tools -and -not $path.StartsWith('daemon-win-x64/', [StringComparison]::Ordinal))) {
            throw 'Unexpected, duplicated or unsafe daemon archive entry.'
        }
        $seen[$path] = $true
    }
    foreach ($required in ($tools + @('daemon-win-x64/runtime.json'))) {
        if (-not $seen.ContainsKey($required)) { throw "Archive is missing $required." }
    }
} finally { $zip.Dispose() }
$fixture = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('Temp\twig-distribution-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
try {
    [IO.Compression.ZipFile]::ExtractToDirectory($Archive, $fixture)
    $runtime = Join-Path $fixture 'daemon-win-x64'
    $cli = Join-Path $fixture 'cli'
    [IO.Compression.ZipFile]::ExtractToDirectory($CliArchive, $cli)
    # Only the certificate/external-command boundary is replaced by this existing fixture.
    & (Join-Path $PSScriptRoot 'test-daemon-install.ps1') -OperatorDirectory $fixture -RuntimeDirectory $runtime
    & $PythonExecutable (Join-Path $PSScriptRoot 'run-bounded.py') --warn-after 60 --timeout 300 -- `
        $PythonExecutable (Join-Path $PSScriptRoot 'test-daemon-commands.py') --cli-dir $cli --daemon-dir $runtime
    if ($LASTEXITCODE -ne 0) { throw 'The independently distributed CLI and daemon failed the isolated command boundary.' }
    Write-Output 'PASS archive checksum/inventory, extracted operator install, immutable selection and independent CLI/runtime command boundary. No TLS/trust/Startup qualification.'
} finally {
    [IO.Directory]::Delete($fixture, $true)
}
