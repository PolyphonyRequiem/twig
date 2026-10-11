param([Parameter(Mandatory=$true)][ValidateSet('panel','open','tree','table','close')][string]$Mode)
$ErrorActionPreference = 'Stop'
# THROWAWAY: invokes the same NativeAOT executable; no Go or private read companion.
$root = $PSScriptRoot
$exe = Join-Path $root 'publish\win-x64\twig-tui-aot-1143.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Publish the isolated AOT1143 project first; no JIT/install fallback.' }
if ($Mode -eq 'panel') {
    & $exe panel
} else {
    $source = $env:HERDR_PANE_ID
    if (-not $source) { throw 'The actual action invocation must supply HERDR_PANE_ID; no focused-pane query fallback.' }
    & $exe $Mode --pane $source
}
exit $LASTEXITCODE
