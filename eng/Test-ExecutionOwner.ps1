param(
    [string] $Server = "$PSScriptRoot\..\src\Twig.Server\bin\Debug\net11.0\Twig.Server.exe",
    [string] $Cli = "$PSScriptRoot\..\src\Twig\bin\Debug\net11.0\twig.exe",
    [Parameter(Mandatory)][string] $OldCli,
    [Parameter(Mandatory)][string] $OldServer,
    [Parameter(Mandatory)][string] $CanvasHost,
    [Parameter(Mandatory)][string] $ProxyHarness,
    [Parameter(Mandatory)][string] $OldCanvasHostDll,
    [Parameter(Mandatory)][string] $Certificate
)
$ErrorActionPreference = 'Stop'
$id = [guid]::NewGuid().ToString('D')
$root = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "Twig.OwnerFixtures\$id"
$homeDir = Join-Path $root home
function Run([string]$exe, [string[]]$arguments, [switch]$Interactive, [switch]$NoMarker) {
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $start.RedirectStandardError = $start.RedirectStandardOutput = $true
    $start.WorkingDirectory = $root
    $start.Environment['TWIG_OWNER_FIXTURE_ID'] = $id
    if ($NoMarker) { [void]$start.Environment.Remove('TWIG_OWNER_FIXTURE_ID') }
    $start.Environment['TWIG_USER_HOME'] = $homeDir
    $start.Environment['TWIG_SERVER_INSTALL_ROOT'] = $root
    foreach ($arg in $arguments) { $start.ArgumentList.Add($arg) }
    $p = [Diagnostics.Process]::Start($start)
    if (!$Interactive) { $p.StandardInput.Close() }
    return $p
}
function Result($process) {
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(25000)) { throw 'Fixture subprocess timed out.' }
        return @{ Code = $process.ExitCode; Out = $stdout.Result; Err = $stderr.Result }
    } finally {
        if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
}
function Require($condition, $message) { if (!$condition) { throw $message } }
$Server = (Resolve-Path $Server).Path
$Cli = (Resolve-Path $Cli).Path
$OldCli = (Resolve-Path $OldCli).Path
$OldServer = (Resolve-Path $OldServer).Path
$CanvasHost = (Resolve-Path $CanvasHost).Path
$ProxyHarness = (Resolve-Path $ProxyHarness).Path
$OldCanvasHostDll = (Resolve-Path $OldCanvasHostDll).Path
$ownerSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
$security = [Security.AccessControl.DirectorySecurity]::new()
$security.SetOwner($ownerSid)
$security.SetAccessRuleProtection($true, $false)
$security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($ownerSid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
[void][IO.FileSystemAclExtensions]::Create([IO.DirectoryInfo]::new($root), $security)
try {
    $worker = Result (Run $Server @('--server-command-worker'))
    Require ($worker.Code -eq 2 -and $worker.Err -match 'owner') 'A qualification worker must refuse before reading an invocation.'
    $unmarked = Result (Run $Server @('--server-command-worker') -NoMarker)
    Require ($unmarked.Code -eq 2 -and $unmarked.Err -match 'owner') 'Removing the fixture selector must not enable a writer in the reserved home.'
    $socket = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $socket.Start()
    $port = $socket.LocalEndpoint.Port
    $socket.Stop()
    [IO.File]::WriteAllLines((Join-Path $root 'owner.fixture'), @("$port", $Certificate, $Server))
    [IO.File]::WriteAllText((Join-Path $root 'current.json'), (@{privateDataDir=$homeDir} | ConvertTo-Json))
    $base = @('--data-dir', $homeDir, '--port', "$port", '--https-certificate', $Certificate)
    $wrong = $base.Clone()
    $wrong[1] = Join-Path $root 'different'
    $failure = Result (Run $Server ($wrong + '--status'))
    Require ($failure.Code -eq 2 -and $failure.Err -match 'selection') 'A conflicting data directory must refuse before probing.'
    $wrong[1] = $homeDir
    $wrong[3] = "$(if ($port -eq 53001) {53002} else {53001})"
    $failure = Result (Run $Server ($wrong + '--ensure-running'))
    Require ($failure.Code -eq 2 -and $failure.Err -match 'selection') 'A conflicting port must refuse before spawn.'
    $startResult = Result (Run $Cli @('server','start','-o','json'))
    Require ($startResult.Code -eq 0 -and ($startResult.Out | ConvertFrom-Json).running) "CLI could not start the fixture owner: $($startResult.Err)"
    $runIdentity = [IO.File]::ReadAllLines((Join-Path $homeDir 'owner.run'))
    $owner = [Diagnostics.Process]::GetProcessById([int]$runIdentity[1])
    Require ($owner.MainModule.FileName -eq $Server -and $owner.StartTime.ToUniversalTime().Ticks -eq [long]$runIdentity[2]) 'Unverified fixture PID.'
    $origin = "https://localhost:$port"
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while ($deadline.Elapsed.TotalSeconds -lt 20) {
        if ($owner.HasExited) { throw 'Owner exited before qualification.' }
        try { $status = Invoke-RestMethod "$origin/api/v1/status" -TimeoutSec 2 -NoProxy; break }
        catch [Net.Http.HttpRequestException], [OperationCanceledException] { Start-Sleep -Milliseconds 100 }
    }
    Require ($status.executionSession -match '^local-execution-v2/') 'The actual daemon must negotiate v2.'
    $statusBytes = [Text.Encoding]::UTF8.GetByteCount((Invoke-WebRequest "$origin/api/v1/status" -NoProxy).Content)
    Require ($statusBytes -le 3072) 'Oversized owner status.'
    $refusal = Result (Run $OldCli @('bench','create','old-client'))
    Require ($refusal.Code -eq 2 -and !$refusal.Out -and $refusal.Err -match 'Incompatible Twig CLI') 'Pinned old CLI must receive understood refusal with no writer.'
    $created = Result (Run $Cli @('bench','create','matched-client','-o','json'))
    Require ($created.Code -eq 0 -and $created.Out -match 'matched-client') "Owner did not accept matched CLI: $($created.Err)"
    $list = Result (Run $Cli @('bench','list','-o','json'))
    Require ($list.Code -eq 0 -and $list.Out -match 'matched-client' -and $list.Out -notmatch 'old-client') 'Only the matched CLI may create a Bench.'
    $selected = Result (Run $Cli @('server','endpoint','-o','json'))
    Require ($selected.Code -eq 0 -and ($selected.Out | ConvertFrom-Json).serverId -eq $id) 'The CLI and Canvas must discover this exact fixture endpoint.'
    $live = Result (Run $Cli @('server','status','-o','json'))
    Require ($live.Code -eq 0 -and ($live.Out | ConvertFrom-Json).running) "Authenticated status failed: $($live.Err)"
    $runPath = Join-Path $homeDir 'owner.run'
    $original = [IO.File]::ReadAllText($runPath)
    try {
        [IO.File]::WriteAllText($runPath, $original.Replace($status.executionSession.Substring(19), [guid]::NewGuid().ToString('D')))
        $foreign = Result (Run $Cli @('server','stop','-o','json'))
        Require ($foreign.Code -ne 0 -and !$owner.HasExited) 'Stop accepted the wrong boot at the same pinned endpoint.'
    } finally { [IO.File]::WriteAllText($runPath, $original) }
    $headers = @{'X-Twig-Bench'='1'; 'X-Twig-Execution'=$status.executionSession}
    $view = Invoke-RestMethod "$origin/api/v1/canvas-views" -Method Post -Body '{}' -ContentType application/json -Headers $headers -NoProxy
    $proxy = Run $CanvasHost @('--canvas-root', $root, '--daemon-root', $root, '--port', '0', '--twig-cli', $Cli) -Interactive
    $proxyOrigin = ($proxy.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(15)).Result | ConvertFrom-Json).url
    $proxy.StandardInput.WriteLine((@{state='Ready';viewId=$view.viewId} | ConvertTo-Json -Compress))
    for ($i=0; $i -lt 80; $i++) {
        $ready = Invoke-RestMethod "$proxyOrigin/canvas-host/status" -NoProxy
        if ($ready.state -ne 'Starting') { break }
        Start-Sleep -Milliseconds 150
    }
    Require ($ready.state -eq 'Ready') "Actual Canvas owner attachment failed: $($ready.code)"
    $profile = Invoke-RestMethod "$proxyOrigin/api/v1/windows-profile" -Method Post -Body '{"storeId":null,"profileId":null}' -ContentType application/json -Headers $headers -UseDefaultCredentials -NoProxy
    $enroll = @{viewId=$view.viewId; profileId=$profile.profileId} | ConvertTo-Json -Compress
    $pair = Invoke-RestMethod "$proxyOrigin/api/v1/windows-enroll" -Method Post -Body $enroll -ContentType application/json -Headers $headers -UseDefaultCredentials -NoProxy
    $protected = "/api/v1/views/$($view.viewId)/benches"
    $headers.Authorization = "Bearer $($pair.contentCapability)"
    $body = Invoke-RestMethod "$proxyOrigin$protected" -Headers $headers -NoProxy
    Require ($body.benches.name -contains 'matched-client') 'Matched Canvas must read the Bench written by the matched CLI.'
    $oldRoot = Join-Path $root old-proxy
    [void][IO.Directory]::CreateDirectory($oldRoot)
    Get-ChildItem -LiteralPath (Split-Path $ProxyHarness) | Copy-Item -Destination $oldRoot -Recurse
    Copy-Item -LiteralPath $OldCanvasHostDll -Destination (Join-Path $oldRoot 'Twig.CanvasHost.dll') -Force
    $oldProxy = Run (Join-Path $oldRoot (Split-Path $ProxyHarness -Leaf)) @('--already-attached-proxy', $origin, $Certificate) -Interactive
    $oldOrigin = $oldProxy.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(15)).GetAwaiter().GetResult()
    if ($oldOrigin -notmatch '^https://localhost:\d+$') {
        if ($oldProxy.WaitForExit(200)) { throw "Pinned Canvas proxy failed: $($oldProxy.StandardError.ReadToEnd())" }
        throw "Pinned Canvas proxy did not return its loopback origin."
    }
    foreach ($path in @($protected, '/api/v1/windows-profile', '/api/v1/canvas-views')) {
        $arguments = @{Uri="$oldOrigin$path"; Headers=$headers; UseDefaultCredentials=$true; NoProxy=$true; SkipHttpErrorCheck=$true}
        if ($path -ne $protected) { $arguments.Method='Post'; $arguments.Body='{}'; $arguments.ContentType='application/json' }
        $reply = Invoke-WebRequest @arguments
        Require ($reply.StatusCode -eq 403 -and ($reply.Content | ConvertFrom-Json).code -eq 'PolicyViolation' -and $reply.Content -notmatch 'matched-client|contentCapability') 'Pinned old proxy must deliver understood 403 even to an already-authorized view.'
    }
    $streamClient = [Net.Http.HttpClient]::new()
    try {
        $streamClient.DefaultRequestHeaders.Add('Authorization', $headers.Authorization)
        $streamClient.DefaultRequestHeaders.Add('X-Twig-Execution', $status.executionSession)
        $stream = $streamClient.GetStreamAsync("$proxyOrigin/api/v1/views/$($view.viewId)/events").WaitAsync([TimeSpan]::FromSeconds(5)).Result
        $reader = [IO.StreamReader]::new($stream)
        $readyFrame = $reader.ReadLine() + "`n" + $reader.ReadLine() + "`n" + $reader.ReadLine() + "`n"
        Require ($readyFrame.StartsWith('event: ready') -and [Text.Encoding]::UTF8.GetByteCount($readyFrame) -le 4096) 'Expected bounded authorized event frame.'
    } finally { if ($reader) { $reader.Dispose() }; $streamClient.Dispose() }
    # Suspend only the process created by this script; a stopped heartbeat must never become authority.
    Add-Type -TypeDefinition @'
public static class TwigFixturePause {
    [System.Runtime.InteropServices.DllImport("ntdll.dll")] public static extern int NtSuspendProcess(System.IntPtr handle);
    [System.Runtime.InteropServices.DllImport("ntdll.dll")] public static extern int NtResumeProcess(System.IntPtr handle);
}
'@
    Require ([TwigFixturePause]::NtSuspendProcess($owner.Handle) -eq 0) 'Cannot pause the owned fixture process.'
    try {
        Start-Sleep -Seconds 30
        $contender = Result (Run $Server $base)
        Require ($contender.Code -ne 0 -and !$owner.HasExited) 'A paused owner lost its OS lease.'
        $ensure = Result (Run $Server ($base + '--ensure-running'))
        Require ($ensure.Code -ne 0) 'Unresponsive is not absent.'
    } finally { [void][TwigFixturePause]::NtResumeProcess($owner.Handle) }
    $again = Invoke-RestMethod "$origin/api/v1/status" -NoProxy
    Require ($again.executionSession -eq $status.executionSession) 'The paused owner was replaced.'
    $legacyStop = Result (Run $Server ($base + '--stop'))
    Require ($legacyStop.Code -ne 0 -and !$owner.HasExited) 'A legacy launcher must not be able to stop the fixture owner.'
    $oldStop = Result (Run $OldServer ($base + '--stop'))
    Require ($oldStop.Code -ne 0 -and $oldStop.Err -match 'does not support CLI stop' -and !$owner.HasExited) 'Pinned old operator unexpectedly acquired a stop signal.'
    $stopped = Result (Run $Cli @('server','stop','-o','json'))
    Require ($stopped.Code -eq 0 -and $owner.WaitForExit(3000)) "Matched Stop must reach only the selected run: $($stopped.Err)"
    Write-Output "PASS: matched owner, CLI and Canvas; pinned refusals; 30-second lease; exact lifecycle. Status $statusBytes bytes, ready frame $([Text.Encoding]::UTF8.GetByteCount($readyFrame)) bytes."
} finally {
    foreach ($hostProcess in @($oldProxy, $proxy)) {
        if ($hostProcess) {
            $hostProcess.StandardInput.Close()
            if (!$hostProcess.WaitForExit(3000)) { $hostProcess.Kill($true); $hostProcess.WaitForExit() }
            $hostProcess.Dispose()
        }
    }
    if ($owner) {
        if (!$owner.HasExited) { $owner.Kill($true); $owner.WaitForExit() }
        $owner.Dispose()
    }
    Remove-Item -LiteralPath $root -Recurse -Force
}
