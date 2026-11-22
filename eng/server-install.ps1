[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $PrivateDataDir,
    [string] $PackageDirectory,
    [int] $Port = 0,
    [string] $HttpsCertificateThumbprint,
    [string] $InstallRoot = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Twig\Server'),
    [string] $CanvasInstallRoot = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Twig\Canvas'),
    [string] $PythonExecutable = 'python3',
    [string[]] $PythonPrefixArgs = @(),
    [switch] $RegisterStartup,
    [string] $StartupDirectory = [Environment]::GetFolderPath('Startup')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT' -or -not [Environment]::Is64BitOperatingSystem) {
    throw 'This installer requires 64-bit Windows.'
}
. (Join-Path $PSScriptRoot 'server-launch.ps1') -InstallRoot $InstallRoot

function Invoke-BenchCommand([string] $File, [string[]] $Arguments, [int] $Seconds) {
    $supervised = @($PythonPrefixArgs) + @((Join-Path $PSScriptRoot 'server-run-owned.py'),
        '--timeout', [string]$Seconds, '--', $File) + $Arguments
    $quoted = @($supervised | ForEach-Object {
        '"' + [regex]::Replace([regex]::Replace($_, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
    })
    $info = New-Object Diagnostics.ProcessStartInfo
    $python = Get-Command $PythonExecutable -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $info.FileName = $python.Source
    $info.WorkingDirectory = if ($PackageDirectory) { $PSScriptRoot } else { Split-Path $PSScriptRoot -Parent }
    $info.Arguments = $quoted -join ' '
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $info
    try {
        [void]$process.Start()
        $process.StandardInput.Close()
        $output = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        # Native execution is bounded by the job supervisor; only its two-second reap gets a margin.
        if (-not $process.WaitForExit(($Seconds + 3) * 1000)) {
            # Killing this supervisor closes its sole job handle, not merely the native root.
            $process.Kill()
            [void]$process.WaitForExit(2000)
            throw "Owned supervisor failed beyond the $Seconds-second native deadline; kill-on-close requested; cleanup unconfirmed."
        }
        if (-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($output, $errors), 2000)) {
            throw "Owned supervisor exited $($process.ExitCode) but output did not close; cleanup unconfirmed."
        }
        if ($process.ExitCode -ne 0) { throw "Owned command exited $($process.ExitCode).`n$($output.Result)$($errors.Result)" }
        return $output.Result.Trim()
    } finally { $process.Dispose() }
}

function Write-BenchAtomic([string] $Path, [string] $Text) {
    $Path = Get-BenchLocalPath $Path
    $temporary = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    [IO.File]::WriteAllText($temporary, $Text, (New-Object Text.UTF8Encoding($false)))
    if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($temporary, $Path, [NullString]::Value) }
    else { [IO.File]::Move($temporary, $Path) }
}

$InstallRoot = Get-BenchLocalPath $InstallRoot
$PrivateDataDir = Get-BenchLocalPath $PrivateDataDir
$CanvasInstallRoot = Get-BenchLocalPath $CanvasInstallRoot
$local = Get-BenchLocalPath ([Environment]::GetFolderPath('LocalApplicationData'))
if (-not $InstallRoot.StartsWith($local + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'InstallRoot must be a dedicated directory beneath this user''s LocalApplicationData.'
}
if ($PrivateDataDir -ieq $InstallRoot -or $PrivateDataDir.StartsWith($InstallRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath $PrivateDataDir -PathType Container)) {
    throw 'Supply an existing private service data directory outside the application installation directory.'
}
if ($Port -lt 1 -or $Port -gt 65535 -or $Port -eq 443 -or
    $HttpsCertificateThumbprint -cnotmatch '^[A-Fa-f0-9]{40}$') {
    throw 'Native panel installation requires an explicit stable port and an existing trusted localhost certificate thumbprint; no certificate is created or trusted.'
}
if ($RegisterStartup) {
    $StartupDirectory = Get-BenchLocalPath $StartupDirectory
    if (-not (Test-Path -LiteralPath $StartupDirectory -PathType Container)) { throw 'Startup directory does not exist.' }
    $shortcutPath = Join-Path $StartupDirectory 'Twig Server.lnk'
    if (Test-Path -LiteralPath $shortcutPath) {
        $existing = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcutPath)
        if ($existing.TargetPath -ine (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -or
            $existing.Arguments -cne ('-NoLogo -NoProfile -File "' + (Join-Path $InstallRoot 'server-launch.ps1') + '"')) {
            throw 'Refusing to replace a foreign Startup shortcut.'
        }
    }
}
[void][IO.Directory]::CreateDirectory($InstallRoot)
$leasePath = Get-BenchLocalPath (Join-Path $InstallRoot '.install.lock')
$lease = New-Object IO.FileStream($leasePath, [IO.FileMode]::OpenOrCreate,
    [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
$previous = $null
if (Test-Path -LiteralPath $InstallRoot) {
    $entries = @(Get-ChildItem -LiteralPath $InstallRoot -Force | Where-Object { $_.Name -ne '.install.lock' })
    if ($entries.Count) {
        $hasCurrent = Test-Path -LiteralPath (Join-Path $InstallRoot 'current.json') -PathType Leaf
        if ($hasCurrent) {
            $null = Get-BenchInstallation $InstallRoot
            $previous = Get-Content -LiteralPath (Join-Path $InstallRoot 'current.json') -Raw -Encoding UTF8
        }
        foreach ($entry in $entries) {
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing reparse-point installation entries.' }
            if (-not $hasCurrent -and ($entry.Name -cnotmatch '^\.stage-[a-f0-9]{32}$' -or -not $entry.PSIsContainer)) {
                throw 'Refusing an unselected or foreign installation directory.'
            }
            if ($entry.Name -notin @('current.json', 'previous.json', 'server-launch.ps1', 'versions', '.install.lock') -and
                $entry.Name -notmatch '^\.stage-[a-f0-9]{32}$' -and
                $entry.Name -notmatch '^(current\.json|previous\.json|server-launch\.ps1)\.[a-f0-9]{32}\.tmp$') {
                throw 'Refusing a foreign installation directory.'
            }
        }
    }
}
$source = Split-Path $PSScriptRoot -Parent
if ($PackageDirectory) {
    # The operator verifies the release archive before extraction. Validate its
    # manifest and closed inventory here and again after copying into staging.
    $PackageDirectory = Get-BenchLocalPath $PackageDirectory
    $packageManifest = Get-BenchLocalPath (Join-Path $PackageDirectory 'runtime.json')
    $packageHash = Get-BenchHash $packageManifest
    $version = (Get-Content -LiteralPath $packageManifest -Raw -Encoding UTF8 | ConvertFrom-Json).version
    $null = Get-BenchRuntime $PackageDirectory $version $packageHash
} else {
    $version = Invoke-BenchCommand 'git' @('-C', $source, 'rev-parse', 'HEAD') 10
}
if ($version -cnotmatch '^[a-f0-9]{40}$') { throw 'Expected an exact Git source commit.' }
if (-not $PackageDirectory -and (Invoke-BenchCommand 'git' @('-C', $source, 'status', '--porcelain', '--untracked-files=normal') 10)) {
    throw 'Publish requires a clean source checkout, including the pinned native submodule.'
}
$versions = Join-Path $InstallRoot 'versions'
$destination = Join-Path $versions $version
if (Test-Path -LiteralPath $destination) { throw 'This version already exists; immutable versions are never overwritten.' }
$stagingRoot = (New-Item -ItemType Directory -Path (Join-Path $InstallRoot ('.stage-' + [Guid]::NewGuid().ToString('N')))).FullName
try {
$stage = Join-Path $stagingRoot ('versions\' + $version)
[void][IO.Directory]::CreateDirectory($stage)
if ($PackageDirectory) {
    Get-ChildItem -LiteralPath $PackageDirectory -Force | Copy-Item -Destination $stage -Recurse
    $null = Get-BenchRuntime $stage $version $packageHash
} else {
$timer = [Diagnostics.Stopwatch]::StartNew()
try {
$publishOutput = Invoke-BenchCommand 'dotnet' @('publish', (Join-Path $source 'src\Twig.Server\Twig.Server.csproj'),
    '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=false',
    '-p:PublishTrimmed=false', ('-p:ServerSourceRevisionId=' + $version), '-m:1', '--verbosity', 'minimal', '-o', $stage) 300
} finally {
    $timer.Stop()
    if ($timer.Elapsed.TotalSeconds -gt 120) {
        Write-Warning 'Self-contained publish exceeded the 120-second timing warning.' -WarningAction Continue
    }
}
Write-Host $publishOutput
Write-Host ('Self-contained publish completed in {0:N2} seconds.' -f $timer.Elapsed.TotalSeconds)
$files = @(Get-ChildItem -LiteralPath $stage -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
    [ordered]@{ path = $_.FullName.Substring($stage.Length + 1); sha256 = Get-BenchHash $_.FullName }
})
$manifest = [ordered]@{ application = 'twig-server'; schema = 1; version = $version; rid = 'win-x64';
    protocol = 'twig-bench-service-v1'; files = $files }
Write-BenchAtomic (Join-Path $stage 'runtime.json') ($manifest | ConvertTo-Json -Depth 5)
}
$record = [ordered]@{
    application = 'twig-server'; schema = 4; version = $version
    manifestHash = Get-BenchHash (Join-Path $stage 'runtime.json')
    privateDataDir = $PrivateDataDir; authority = 'native-user-benches-v1'
    port = $Port; httpsCertificateThumbprint = $HttpsCertificateThumbprint
    canvasInstallRoot = $CanvasInstallRoot
}
$candidate = $record | ConvertTo-Json -Depth 3
# Reject incomplete packages before any executable or certificate preflight is attempted.
$null = Get-BenchInstallation $stagingRoot ($candidate | ConvertFrom-Json)
Write-Host (Invoke-BenchCommand (Join-Path $stage 'Twig.Server.exe') @('--check-certificate', $HttpsCertificateThumbprint) 10)
[void][IO.Directory]::CreateDirectory($versions)
[IO.Directory]::Move($stage, $destination)
} finally {
    [IO.Directory]::Delete((Get-BenchLocalPath $stagingRoot), $true)
}
$oldRecord = Join-Path $InstallRoot 'current.json'
# Validate the complete promoted version again before selecting its record.
$null = Get-BenchInstallation $InstallRoot ($candidate | ConvertFrom-Json)
# The current record switches the complete runtime and operator paths together.
if ($null -ne $previous) { Write-BenchAtomic (Join-Path $InstallRoot 'previous.json') $previous }
Write-BenchAtomic (Join-Path $InstallRoot 'server-launch.ps1') (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'server-launch.ps1') -Raw -Encoding UTF8)
Write-BenchAtomic $oldRecord $candidate
if ($RegisterStartup) {
    $pendingShortcut = Join-Path $StartupDirectory ('.Twig Server.' + [Guid]::NewGuid().ToString('N') + '.lnk')
    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($pendingShortcut)
    $shortcut.TargetPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $shortcut.Arguments = '-NoLogo -NoProfile -File "' + (Join-Path $InstallRoot 'server-launch.ps1') + '"'
    $shortcut.WorkingDirectory = $InstallRoot
    $shortcut.WindowStyle = 7
    $shortcut.Description = 'Twig panel: Windows sign-in; restore this console for explicit stop.'
    $shortcut.Save()
    if (Test-Path -LiteralPath $shortcutPath) { [IO.File]::Replace($pendingShortcut, $shortcutPath, [NullString]::Value) }
    else { [IO.File]::Move($pendingShortcut, $shortcutPath) }
}
Write-Host "Installed Twig server version $version; no server was started or stopped and no canvas was installed."
if ($RegisterStartup) { Write-Host 'Sign-in startup registered; the native panel uses same-owner Windows authentication without pairing codes.' }
} finally { $lease.Dispose() }
