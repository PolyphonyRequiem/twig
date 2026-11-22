param(
    [string] $OperatorDirectory = $PSScriptRoot,
    [string] $RuntimeDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'test-server-paths.ps1') -OperatorDirectory $OperatorDirectory
$fixture = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('Temp\twig-server-install-' + [Guid]::NewGuid().ToString('N'))
$tools = Join-Path $fixture 'source\tools'
[void][IO.Directory]::CreateDirectory($tools)
$tokens = $null; $errors = $null
$original = Join-Path $OperatorDirectory 'server-install.ps1'
$ast = [Management.Automation.Language.Parser]::ParseFile($original, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Server installer parse failed.' }
$functions = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Invoke-BenchCommand'
}, $true))
if ($functions.Count -ne 1) { throw 'Expected one owned external-command boundary.' }
$stub = @'
function Invoke-BenchCommand([string] $File, [string[]] $Arguments, [int] $Seconds) {
    if ($global:ServerFixture.Mode -ceq 'package-only' -and [IO.Path]::GetFileName($File) -cne 'Twig.Server.exe') {
        throw 'Package installation unexpectedly required source or build tools.'
    }
    if ($File -ceq 'git') {
        if ($Arguments -contains 'rev-parse') { return $global:ServerFixture.Version }
        if ($Arguments -contains 'status') { return '' }
    } elseif ($File -ceq 'dotnet' -and $Arguments[0] -ceq 'publish') {
        if ($Arguments -cnotcontains ('-p:ServerSourceRevisionId=' + $global:ServerFixture.Version) -or $Seconds -ne 300) {
            throw 'The server publish boundary lost its exact source stamp or deadline.'
        }
        $output = $Arguments[[Array]::IndexOf($Arguments, '-o') + 1]
        foreach ($name in $global:ServerFixture.Names) {
            if ($global:ServerFixture.Mode -ceq 'missing-native-provider' -and $name -ceq 'Twig.Providers.Native.dll') { continue }
            $path = Join-Path $output $name
            [IO.File]::WriteAllText($path, 'Synthetic binary. Never execute.')
            if ($global:ServerFixture.Mode -ceq 'publish-failure') { throw 'Synthetic publish failure.' }
        }
        if ($global:ServerFixture.Mode -ceq 'bundled-ui') {
            [void][IO.Directory]::CreateDirectory((Join-Path $output 'wwwroot'))
            [IO.File]::WriteAllText((Join-Path $output 'wwwroot\app.js'), 'Forbidden bundled UI.')
        }
        if ($global:ServerFixture.Mode -ceq 'bundled-cli') {
            [IO.File]::WriteAllText((Join-Path $output 'twig.exe'), 'Forbidden independent CLI.')
        }
        return 'Synthetic server publish.'
    } elseif ([IO.Path]::GetFileName($File) -ceq 'Twig.Server.exe' -and
        $Arguments.Count -eq 2 -and $Arguments[0] -ceq '--check-certificate') {
        if ($global:ServerFixture.Mode -ceq 'certificate-refusal') { throw 'Synthetic certificate refusal.' }
        return 'Synthetic existing-certificate check; no certificate provider was accessed.'
    }
    throw ('Unexpected external command: ' + $File)
}
'@
$function = $functions[0].Extent
$text = [IO.File]::ReadAllText($original)
$text = $text.Substring(0, $function.StartOffset) + $stub + $text.Substring($function.EndOffset)
[IO.File]::WriteAllText((Join-Path $tools 'server-install.ps1'), $text)
[IO.File]::Copy((Join-Path $OperatorDirectory 'server-launch.ps1'), (Join-Path $tools 'server-launch.ps1'))
$data = Join-Path $fixture 'data'
[void][IO.Directory]::CreateDirectory($data)
$global:ServerFixture = @{ Version = '1' * 40; Mode = '';
    Names = @('Twig.Server.exe', 'Twig.Server.dll', 'coreclr.dll', 'hostfxr.dll',
        'Twig.Providers.Native.dll', 'Twig.Infrastructure.dll', 'Twig.Store.Sqlite.dll') }
try {
    foreach ($mode in @('publish-failure', 'missing-native-provider', 'bundled-ui', 'bundled-cli', 'certificate-refusal')) {
        $root = Join-Path $fixture $mode
        $arguments = @{ PrivateDataDir = $data; InstallRoot = $root; Port = 53219;
            HttpsCertificateThumbprint = 'A' * 40; CanvasInstallRoot = Join-Path $fixture 'not-installed-canvas' }
        $global:ServerFixture.Mode = $mode
        $rejected = $false
        try { & (Join-Path $tools 'server-install.ps1') @arguments | Out-Null } catch { $rejected = $true }
        if (-not $rejected -or (Test-Path -LiteralPath (Join-Path $root 'current.json')) -or
            (Test-Path -LiteralPath (Join-Path $root ('versions\' + $global:ServerFixture.Version))) -or
            @(Get-ChildItem -LiteralPath $root -Force | Where-Object Name -Like '.stage-*').Count) {
            throw "Failed server stage was selected or poisoned: $mode"
        }
        $global:ServerFixture.Mode = 'complete'
        & (Join-Path $tools 'server-install.ps1') @arguments | Out-Null
        $record = Get-Content -LiteralPath (Join-Path $root 'current.json') -Raw | ConvertFrom-Json
        if ($record.application -cne 'twig-server' -or $record.schema -ne 4 -or
            $record.canvasInstallRoot -cne $arguments.CanvasInstallRoot) { throw 'Independent server selection was not retained.' }
    }
    $retained = [IO.File]::ReadAllText((Join-Path $root 'current.json'))
    $global:ServerFixture.Version = '2' * 40
    & (Join-Path $tools 'server-install.ps1') @arguments | Out-Null
    if ([IO.File]::ReadAllText((Join-Path $root 'previous.json')) -cne $retained) { throw 'Exact previous server selection was lost.' }
    . (Join-Path $tools 'server-launch.ps1')
    $record = Get-Content -LiteralPath (Join-Path $root 'current.json') -Raw | ConvertFrom-Json
    $null = Get-BenchInstallation $root $record
    $currentText = [IO.File]::ReadAllText((Join-Path $root 'current.json'))
    $global:ServerFixture.Version = '3' * 40
    $global:ServerFixture.Mode = 'certificate-refusal'
    $refused = $false
    try { & (Join-Path $tools 'server-install.ps1') @arguments | Out-Null } catch { $refused = $true }
    if (-not $refused -or [IO.File]::ReadAllText((Join-Path $root 'current.json')) -cne $currentText -or
        [IO.File]::ReadAllText((Join-Path $root 'previous.json')) -cne $retained) {
        throw 'A refused upgrade changed the current or rollback selection.'
    }
    $null = Get-BenchInstallation $root ($retained | ConvertFrom-Json)
    $global:ServerFixture.Version = '2' * 40
    $packageArguments = $arguments.Clone()
    $packageArguments.InstallRoot = Join-Path $fixture 'packaged-install'
    $packageArguments.PackageDirectory = Join-Path $root ('versions\' + $record.version)
    $global:ServerFixture.Mode = 'package-only'
    & (Join-Path $tools 'server-install.ps1') @packageArguments | Out-Null
    $null = Get-BenchInstallation $packageArguments.InstallRoot
    if ($RuntimeDirectory) {
        $actualArguments = $packageArguments.Clone()
        $actualArguments.PackageDirectory = (Resolve-Path -LiteralPath $RuntimeDirectory).Path
        $actualArguments.InstallRoot = Join-Path $fixture 'actual-release-install'
        & (Join-Path $tools 'server-install.ps1') @actualArguments | Out-Null
        $installed = Get-BenchInstallation $actualArguments.InstallRoot
        if ([IO.File]::ReadAllText((Join-Path $actualArguments.InstallRoot 'server-launch.ps1')) -cne
            [IO.File]::ReadAllText((Join-Path $OperatorDirectory 'server-launch.ps1')) -or
            (Get-BenchHash $installed.Executable) -cne
            (Get-BenchHash (Join-Path $actualArguments.PackageDirectory 'Twig.Server.exe'))) {
            throw 'The extracted runtime and launcher were not installed intact.'
        }
        $refused = $false
        try { & (Join-Path $tools 'server-install.ps1') @actualArguments | Out-Null } catch { $refused = $true }
        if (-not $refused) { throw 'Immutable release reinstall was not refused.' }
    }
    # Only the certificate-store boundary is synthetic; selection/runtime validation remains real.
    $global:ServerFixture.CertificateChecks = 0
    function Assert-BenchEndpointCertificate($Installation) {
        $global:ServerFixture.CertificateChecks++
        if ($Installation.Certificate -cne ('A' * 40) -or
            [IO.Path]::GetFileName($Installation.Executable) -cne 'Twig.Server.exe') {
            throw 'Unexpected endpoint certificate selection.'
        }
        if ($global:ServerFixture.Mode -ceq 'certificate-refusal') { throw 'Synthetic certificate refusal.' }
        if ($global:ServerFixture.Mode -ceq 'selection-race') {
            [IO.File]::AppendAllText((Join-Path $root 'current.json'), ' ')
        }
    }
    $selectedEndpoint = Get-BenchEndpoint $root
    if ($selectedEndpoint.origin -cne 'https://localhost:53219' -or
        $selectedEndpoint.certificateThumbprint -cne ('A' * 40) -or $null -ne $selectedEndpoint.serverId -or
        $selectedEndpoint.selectionId -cnotmatch '^[A-F0-9]{64}$' -or
        (Get-BenchEndpoint $root).selectionId -cne $selectedEndpoint.selectionId -or
        (Test-Path -LiteralPath (Join-Path $data 'server.id')) -or
        (Test-Path -LiteralPath (Join-Path $data 'server.lock'))) {
        throw 'Prestart endpoint discovery changed data or returned an unstable binding.'
    }
    $identity = [Guid]::NewGuid().ToString('D')
    [IO.File]::WriteAllText((Join-Path $data 'server.id'), $identity)
    $started = Get-BenchEndpoint $root
    if ($started.serverId -cne $identity -or $started.selectionId -cne $selectedEndpoint.selectionId -or
        [Text.Encoding]::UTF8.GetByteCount(($started | ConvertTo-Json -Compress)) -gt 4096) {
        throw 'Durable identity was not projected independently of selected installation.'
    }
    foreach ($invalid in @('', 'invalid', [Guid]::Empty.ToString('D'), ('a' * 65))) {
        [IO.File]::WriteAllText((Join-Path $data 'server.id'), $invalid)
        $refused = $false
        try { Get-BenchEndpoint $root | Out-Null } catch { $refused = $true }
        if (-not $refused) { throw 'Invalid durable identity was treated as absent.' }
    }
    [IO.File]::WriteAllText((Join-Path $data 'server.id'), $identity)
    $selectedText = [IO.File]::ReadAllText((Join-Path $root 'current.json'))
    foreach ($field in @('port', 'canvasInstallRoot', 'version')) {
        $changed = $record | ConvertTo-Json | ConvertFrom-Json
        switch ($field) {
            'port' { $changed.port = 53220 }
            'canvasInstallRoot' { $changed.canvasInstallRoot = Join-Path $fixture 'other-canvas' }
            'version' {
                $changed.version = '1' * 40
                $changed.manifestHash = Get-BenchHash (Join-Path $root ('versions\' + $changed.version + '\runtime.json'))
            }
        }
        [IO.File]::WriteAllText((Join-Path $root 'current.json'), ($changed | ConvertTo-Json))
        if ((Get-BenchEndpoint $root).selectionId -ceq $selectedEndpoint.selectionId) {
            throw "Selection identity did not change with $field."
        }
    }
    [IO.File]::WriteAllText((Join-Path $root 'current.json'), $selectedText)
    foreach ($mode in @('certificate-refusal', 'selection-race')) {
        $global:ServerFixture.Mode = $mode
        $refused = $false
        try { Get-BenchEndpoint $root | Out-Null } catch { $refused = $true }
        if (-not $refused) { throw "Unverified endpoint was exposed: $mode" }
        [IO.File]::WriteAllText((Join-Path $root 'current.json'), $selectedText)
    }
    $global:ServerFixture.Mode = 'complete'
    foreach ($field in @('authority', 'port', 'privateDataDir', 'canvasInstallRoot')) {
        $changed = $record | ConvertTo-Json | ConvertFrom-Json
        $changed.$field = switch ($field) { 'port' { 443 }; 'privateDataDir' { $root }; 'canvasInstallRoot' { $root }; default { 'foreign' } }
        $refused = $false
        try { Get-BenchInstallation $root $changed | Out-Null } catch { $refused = $true }
        if (-not $refused) { throw "Invalid server binding was accepted: $field" }
    }
    $binary = Join-Path $root ('versions\' + $global:ServerFixture.Version + '\Twig.Providers.Native.dll')
    [IO.File]::AppendAllText($binary, ' changed')
    $refused = $false
    try { Get-BenchInstallation $root $record | Out-Null } catch { $refused = $true }
    if (-not $refused) { throw 'Tampered server binary was accepted.' }
    $checks = $global:ServerFixture.CertificateChecks
    $refused = $false
    try { Get-BenchEndpoint $root | Out-Null } catch { $refused = $true }
    if (-not $refused -or $global:ServerFixture.CertificateChecks -ne $checks) {
        throw 'Endpoint discovery reached a tampered executable.'
    }
    Write-Output 'PASS standalone server install and endpoint: prestart/durable identity, stable/changed selection, certificate refusal, selection race, binding and integrity guards; no real runtime/certificate/Startup operation'
} finally {
    Remove-Variable ServerFixture -Scope Global
    [IO.Directory]::Delete($fixture, $true)
}
