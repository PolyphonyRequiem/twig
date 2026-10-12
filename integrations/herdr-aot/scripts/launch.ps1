param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('open', 'table', 'tree', 'review', 'exit-review', 'reconnect', 'status', 'close')]
    [string] $Intent,
    [string] $File
)
$ErrorActionPreference = 'Stop'
$root = $env:HERDR_PLUGIN_ROOT
if (-not $root) { throw 'HERDR_PLUGIN_ROOT is required; no installed stable executable fallback.' }
# PowerShell 5 provider paths do not consistently accept Win32 extended prefixes.
# Preserve UNC roots instead of incorrectly turning \\?\UNC\server into UNC\server.
if ($root.StartsWith('\\?\UNC\', [System.StringComparison]::OrdinalIgnoreCase)) { $root = '\\' + $root.Substring(8) }
elseif ($root.StartsWith('\\?\', [System.StringComparison]::Ordinal)) { $root = $root.Substring(4) }
$binary = Join-Path $root 'bin\twig.exe'
if (-not [System.IO.File]::Exists($binary)) { throw "Native candidate payload is absent: $binary. Delivery/signing qualification must supply this isolated artifact." }
$arguments = @('bench', 'browser', '--herdr', '--intent', $Intent)
if ($File) {
    if ($Intent -ne 'review') { throw '--File is only valid for review.' }
    $arguments += @('--file', $File)
}
& $binary @arguments
exit $LASTEXITCODE
