Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('Temp\twig-daemon-install-' + [Guid]::NewGuid().ToString('N'))
$tools = Join-Path $fixture 'source\tools'
[void][IO.Directory]::CreateDirectory($tools)
$tokens = $null; $errors = $null
$original = Join-Path $PSScriptRoot 'daemon-install.ps1'
$ast = [Management.Automation.Language.Parser]::ParseFile($original, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Daemon installer parse failed.' }
$functions = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Invoke-BenchCommand'
}, $true))
if ($functions.Count -ne 1) { throw 'Expected one owned external-command boundary.' }
$stub = @'
function Invoke-BenchCommand([string] $File, [string[]] $Arguments, [int] $Seconds) {
    if ($File -ceq 'git') {
        if ($Arguments -contains 'rev-parse') { return $global:DaemonFixture.Version }
        if ($Arguments -contains 'status') { return '' }
    } elseif ($File -ceq 'dotnet' -and $Arguments[0] -ceq 'publish') {
        if ($Arguments -cnotcontains ('-p:DaemonSourceRevisionId=' + $global:DaemonFixture.Version) -or $Seconds -ne 120) {
            throw 'The daemon publish boundary lost its exact source stamp or deadline.'
        }
        $output = $Arguments[[Array]::IndexOf($Arguments, '-o') + 1]
        foreach ($name in $global:DaemonFixture.Names) {
            if ($global:DaemonFixture.Mode -ceq 'missing-native-provider' -and $name -ceq 'Twig.Providers.Native.dll') { continue }
            $path = Join-Path $output $name
            [IO.File]::WriteAllText($path, 'Synthetic binary. Never execute.')
            if ($global:DaemonFixture.Mode -ceq 'publish-failure') { throw 'Synthetic publish failure.' }
        }
        if ($global:DaemonFixture.Mode -ceq 'bundled-ui') {
            [void][IO.Directory]::CreateDirectory((Join-Path $output 'wwwroot'))
            [IO.File]::WriteAllText((Join-Path $output 'wwwroot\app.js'), 'Forbidden bundled UI.')
        }
        return 'Synthetic daemon publish.'
    } elseif ([IO.Path]::GetFileName($File) -ceq 'Bench.Server.exe' -and
        $Arguments.Count -eq 2 -and $Arguments[0] -ceq '--check-certificate') {
        if ($global:DaemonFixture.Mode -ceq 'certificate-refusal') { throw 'Synthetic certificate refusal.' }
        return 'Synthetic existing-certificate check; no certificate provider was accessed.'
    }
    throw ('Unexpected external command: ' + $File)
}
'@
$function = $functions[0].Extent
$text = [IO.File]::ReadAllText($original)
$text = $text.Substring(0, $function.StartOffset) + $stub + $text.Substring($function.EndOffset)
[IO.File]::WriteAllText((Join-Path $tools 'daemon-install.ps1'), $text)
[IO.File]::Copy((Join-Path $PSScriptRoot 'daemon-launch.ps1'), (Join-Path $tools 'daemon-launch.ps1'))
$data = Join-Path $fixture 'data'
[void][IO.Directory]::CreateDirectory($data)
$global:DaemonFixture = @{ Version = '1' * 40; Mode = '';
    Names = @('Bench.Server.exe', 'Bench.Server.dll', 'coreclr.dll', 'hostfxr.dll',
        'Twig.Providers.Native.dll', 'Twig.Infrastructure.dll', 'Twig.Store.Sqlite.dll') }
try {
    foreach ($mode in @('publish-failure', 'missing-native-provider', 'bundled-ui', 'certificate-refusal')) {
        $root = Join-Path $fixture $mode
        $arguments = @{ PrivateDataDir = $data; InstallRoot = $root; Port = 53219;
            HttpsCertificateThumbprint = 'A' * 40; CanvasInstallRoot = Join-Path $fixture 'not-installed-canvas' }
        $global:DaemonFixture.Mode = $mode
        $rejected = $false
        try { & (Join-Path $tools 'daemon-install.ps1') @arguments | Out-Null } catch { $rejected = $true }
        if (-not $rejected -or (Test-Path -LiteralPath (Join-Path $root 'current.json')) -or
            (Test-Path -LiteralPath (Join-Path $root ('versions\' + $global:DaemonFixture.Version))) -or
            @(Get-ChildItem -LiteralPath $root -Force | Where-Object Name -Like '.stage-*').Count) {
            throw "Failed daemon stage was selected or poisoned: $mode"
        }
        $global:DaemonFixture.Mode = 'complete'
        & (Join-Path $tools 'daemon-install.ps1') @arguments | Out-Null
        $record = Get-Content -LiteralPath (Join-Path $root 'current.json') -Raw | ConvertFrom-Json
        if ($record.application -cne 'twig-daemon' -or $record.schema -ne 4 -or
            $record.canvasInstallRoot -cne $arguments.CanvasInstallRoot) { throw 'Independent daemon selection was not retained.' }
    }
    $retained = [IO.File]::ReadAllText((Join-Path $root 'current.json'))
    $global:DaemonFixture.Version = '2' * 40
    & (Join-Path $tools 'daemon-install.ps1') @arguments | Out-Null
    if ([IO.File]::ReadAllText((Join-Path $root 'previous.json')) -cne $retained) { throw 'Exact previous daemon selection was lost.' }
    . (Join-Path $tools 'daemon-launch.ps1')
    $record = Get-Content -LiteralPath (Join-Path $root 'current.json') -Raw | ConvertFrom-Json
    $null = Get-BenchInstallation $root $record
    foreach ($field in @('authority', 'port', 'privateDataDir', 'canvasInstallRoot')) {
        $changed = $record | ConvertTo-Json | ConvertFrom-Json
        $changed.$field = switch ($field) { 'port' { 443 }; 'privateDataDir' { $root }; 'canvasInstallRoot' { $root }; default { 'foreign' } }
        $refused = $false
        try { Get-BenchInstallation $root $changed | Out-Null } catch { $refused = $true }
        if (-not $refused) { throw "Invalid daemon binding was accepted: $field" }
    }
    $binary = Join-Path $root ('versions\' + $global:DaemonFixture.Version + '\Twig.Providers.Native.dll')
    [IO.File]::AppendAllText($binary, ' changed')
    $refused = $false
    try { Get-BenchInstallation $root $record | Out-Null } catch { $refused = $true }
    if (-not $refused) { throw 'Tampered daemon binary was accepted.' }
    Write-Output 'PASS standalone daemon install: failed-stage retry, missing provider, forbidden UI, certificate refusal, exact previous selection, binding and integrity guards; no real runtime/certificate/Startup operation'
} finally {
    Remove-Variable DaemonFixture -Scope Global
    [IO.Directory]::Delete($fixture, $true)
}
