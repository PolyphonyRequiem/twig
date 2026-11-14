[CmdletBinding()]
param([string] $InstallRoot, [switch] $EnsureRunning)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $PSBoundParameters.ContainsKey('InstallRoot')) { $InstallRoot = $PSScriptRoot }

function Get-BenchLocalPath([string] $Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -ne $Path.Trim() -or
        $Path -notmatch '^[A-Za-z]:\\' -or $Path -match '[\x00-\x1f"*?<>|]' -or $Path.Substring(2).Contains(':')) {
        throw 'An explicit absolute local path is required.'
    }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if ($full.Length -le 3) { throw 'A dedicated directory or file path is required, not a drive root.' }
    $cursor = $full
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'Reparse-point paths are not supported.'
            }
        }
        $parent = [IO.Path]::GetDirectoryName($cursor)
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
    return $full
}

function Assert-BenchFields($Object, [string[]] $Fields) {
    if ($null -eq $Object -or
        (@($Object.PSObject.Properties.Name | Sort-Object) -join ',') -cne
        (@($Fields | Sort-Object) -join ',')) { throw 'Invalid installation metadata fields.' }
}

function Get-BenchHash([string] $Path) {
    $stream = [IO.File]::OpenRead($Path)
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-', '') }
    finally { $hash.Dispose(); $stream.Dispose() }
}

function Get-BenchInstallation([string] $Root, $Record = $null) {
    $Root = Get-BenchLocalPath $Root
    if ($null -eq $Record) {
        $recordPath = Get-BenchLocalPath (Join-Path $Root 'current.json')
        $Record = Get-Content -LiteralPath $recordPath -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    $native = $true
    $fields = @('application', 'schema', 'version', 'manifestHash', 'privateDataDir',
        'authority', 'port', 'httpsCertificateThumbprint', 'migrateLegacy', 'canvasInstallRoot')
    Assert-BenchFields $record $fields
    if ($record.application -isnot [string] -or $record.application -cne 'twig-daemon' -or
        ($record.schema -isnot [int] -and $record.schema -isnot [long]) -or $record.schema -ne 4 -or
        $record.version -isnot [string] -or $record.manifestHash -isnot [string] -or
        $record.version -cnotmatch '^[a-f0-9]{40}$' -or $record.manifestHash -cnotmatch '^[A-F0-9]{64}$') {
        throw 'Unsupported or foreign installation metadata.'
    }
    $versionRoot = Get-BenchLocalPath (Join-Path $Root ('versions\' + $record.version))
    $executable = Get-BenchRuntime $versionRoot $record.version $record.manifestHash
    if ($record.privateDataDir -isnot [string]) {
        throw 'Invalid operator path metadata.'
    }
    $data = Get-BenchLocalPath $record.privateDataDir
    if ($data -ieq $Root -or $data.StartsWith($Root + '\', [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $data -PathType Container)) {
        throw 'An existing separate private service data directory is required.'
    }
    if ($record.authority -isnot [string] -or $record.authority -cne 'native-user-benches-v1' -or
        $record.migrateLegacy -isnot [bool] -or $record.canvasInstallRoot -isnot [string]) {
        throw 'Invalid native authority or canvas selection metadata.'
    }
    $canvas = Get-BenchLocalPath $record.canvasInstallRoot
    if ($canvas -ieq $Root -or $canvas.StartsWith($Root + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The canvas installation must be independent of daemon versions.'
    }
    if (($record.port -isnot [int] -and $record.port -isnot [long]) -or $record.port -lt 1 -or
        $record.port -gt 65535 -or $record.port -eq 443 -or
        $record.httpsCertificateThumbprint -isnot [string] -or $record.httpsCertificateThumbprint -cnotmatch '^[A-Fa-f0-9]{40}$') {
        throw 'Invalid trusted localhost endpoint metadata.'
    }
    return [pscustomobject]@{ Executable = $executable; Data = $data; Canvas = $canvas;
        Port = $record.port; Certificate = $record.httpsCertificateThumbprint; Native = $native; MigrateLegacy = $record.migrateLegacy }
}

function Get-BenchRuntime([string] $VersionRoot, [string] $Version, [string] $ManifestHash) {
    $versionRoot = Get-BenchLocalPath $VersionRoot
    if ($Version -cnotmatch '^[a-f0-9]{40}$' -or $ManifestHash -cnotmatch '^[A-F0-9]{64}$') {
        throw 'An exact daemon version and manifest digest are required.'
    }
    $manifestPath = Join-Path $versionRoot 'runtime.json'
    $manifestPath = Get-BenchLocalPath $manifestPath
    if ((Get-BenchHash $manifestPath) -cne $ManifestHash) {
        throw 'Runtime manifest does not match the selected version.'
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-BenchFields $manifest @('application', 'schema', 'version', 'rid', 'protocol', 'files')
    if ($manifest.application -isnot [string] -or $manifest.application -cne 'twig-daemon' -or
        ($manifest.schema -isnot [int] -and $manifest.schema -isnot [long]) -or $manifest.schema -ne 1 -or
        $manifest.version -isnot [string] -or $manifest.rid -isnot [string] -or
        $manifest.version -cne $Version -or $manifest.rid -cne 'win-x64' -or
        $manifest.protocol -cne 'twig-bench-service-v1' -or
        $manifest.files -isnot [array] -or $manifest.files.Count -lt 1) { throw 'Invalid runtime manifest.' }
    $listed = @{}
    foreach ($file in $manifest.files) {
        Assert-BenchFields $file @('path', 'sha256')
        if ($file.path -isnot [string] -or $file.path -notmatch '^[A-Za-z0-9_.-]+(\\[A-Za-z0-9_.-]+)*$' -or
            @($file.path.Split('\') | Where-Object { $_ -in @('.', '..') }).Count -or
            $file.path -ieq 'runtime.json' -or $listed.ContainsKey($file.path) -or
            $file.sha256 -isnot [string] -or $file.sha256 -cnotmatch '^[A-F0-9]{64}$') { throw 'Unsafe runtime manifest entry.' }
        $listed[$file.path] = $true
        $path = Get-BenchLocalPath (Join-Path $versionRoot $file.path)
        if ((Get-BenchHash $path) -cne $file.sha256) {
            throw 'Runtime file failed integrity validation.'
        }
    }
    $entries = @(Get-ChildItem -LiteralPath $versionRoot -Recurse -Force)
    if (@($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
        throw 'Runtime contains reparse points.'
    }
    $actual = @($entries | Where-Object { -not $_.PSIsContainer })
    if ($actual.Count -ne $listed.Count + 1) { throw 'Runtime contains unlisted files.' }
    $requiredFiles = @('Bench.Server.exe', 'Bench.Server.dll', 'coreclr.dll', 'hostfxr.dll',
        'Twig.Providers.Native.dll', 'Twig.Infrastructure.dll', 'Twig.Store.Sqlite.dll')
    if (@($listed.Keys | Where-Object { $_ -like 'wwwroot\*' }).Count) {
        throw 'A daemon package must not bundle presentation assets.'
    }
    foreach ($required in $requiredFiles) {
        if (-not $listed.ContainsKey($required)) { throw 'Runtime publish is incomplete or not self-contained.' }
    }
    return (Join-Path $versionRoot 'Bench.Server.exe')
}

if ($MyInvocation.InvocationName -eq '.') { return }
try {
    $installation = Get-BenchInstallation $InstallRoot
    $arguments = @('--data-dir', $installation.Data, '--port', [string]$installation.Port)
    if ($null -ne $installation.Certificate) { $arguments += @('--https-certificate', $installation.Certificate) }
    $arguments += @('--canvas-root', $installation.Canvas)
    if ($installation.MigrateLegacy) { $arguments += '--migrate-legacy' }
    if ($EnsureRunning) { $arguments += '--ensure-running' }
    # Keep the operator console available for explicit stop; native sign-in never uses pairing codes.
    & $installation.Executable @arguments
    if ($LASTEXITCODE -ne 0) { throw "Twig Bench exited with code $LASTEXITCODE; no existing daemon was stopped." }
} catch {
    [Console]::Error.WriteLine('Twig Bench could not start: ' + $_.Exception.Message)
    if (-not $EnsureRunning -and [Environment]::UserInteractive -and -not [Console]::IsInputRedirected) {
        [void](Read-Host 'Press Enter to close this console')
    }
    exit 1
}
