[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Output,
    [string] $PythonExecutable = 'python3'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'daemon-launch.ps1')
$source = Split-Path $PSScriptRoot -Parent
$Output = Get-BenchLocalPath $Output
$artifacts = (Get-BenchLocalPath (Join-Path $source 'artifacts')) + '\'
if (-not $Output.StartsWith($artifacts, [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $Output)) {
    throw 'Packaging requires a new owned output directory beneath this checkout''s artifacts directory.'
}
$version = (& git -C $source rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $version -cnotmatch '^[a-f0-9]{40}$') { throw 'Exact source revision required.' }
$dirty = & git -C $source status --porcelain --untracked-files=normal
if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Daemon packaging requires a clean source checkout.' }
& $PythonExecutable (Join-Path $source 'tools\run-bounded.py') --timeout 120 -- dotnet publish `
    (Join-Path $source 'src\Twig.Daemon\Twig.Daemon.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:PublishTrimmed=false "-p:DaemonSourceRevisionId=$version" '-m:1' --verbosity minimal -o $Output
if ($LASTEXITCODE -ne 0) { throw 'Daemon publish failed; no package was selected or installed.' }
$files = @(Get-ChildItem -LiteralPath $Output -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
    [ordered]@{ path = $_.FullName.Substring($Output.Length + 1); sha256 = Get-BenchHash $_.FullName }
})
$manifest = [ordered]@{ application = 'twig-daemon'; schema = 1; version = $version; rid = 'win-x64';
    protocol = 'twig-bench-service-v1'; files = $files }
[IO.File]::WriteAllText((Join-Path $Output 'runtime.json'), ($manifest | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
$null = Get-BenchRuntime $Output $version (Get-BenchHash (Join-Path $Output 'runtime.json'))
Write-Output "Packaged verified daemon $version. No installation, certificate, Startup or process state was changed."
