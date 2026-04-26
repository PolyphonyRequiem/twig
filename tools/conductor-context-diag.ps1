<#
.SYNOPSIS
    Measures the context budget of a conductor workflow — MCP tool schemas,
    prompt sizes, and agent-level context estimates.

.DESCRIPTION
    Parses a conductor workflow YAML to identify all MCP servers and agent prompts,
    then probes each MCP server to measure actual tool schema sizes. Produces a
    per-agent context budget report showing what fills each agent's context window.

    Use this to diagnose quality issues caused by context pressure — when agents
    have too many tool schemas or oversized prompts competing for attention.

.PARAMETER Workflow
    Path or registry reference to the conductor workflow YAML file.
    Supports both local paths and registry references (e.g., twig-sdlc-implement@twig).

.PARAMETER SkipMcpProbe
    Skip live MCP server probing and use cached/estimated sizes instead.
    Faster but less accurate.

.PARAMETER Verbose
    Show per-tool breakdowns, not just per-server totals.

.EXAMPLE
    .\conductor-context-diag.ps1 -Workflow twig-sdlc-implement@twig
    .\conductor-context-diag.ps1 -Workflow .\my-workflow.yaml -Verbose
    .\conductor-context-diag.ps1 -Workflow twig-sdlc-planning@twig -SkipMcpProbe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [string]$Workflow,

    [switch]$SkipMcpProbe
)

$ErrorActionPreference = 'Stop'

# ── Resolve workflow path ──────────────────────────────────────────────
function Resolve-WorkflowPath {
    param([string]$Ref)

    if (Test-Path $Ref) { return (Resolve-Path $Ref).Path }

    # Registry reference: name@registry
    if ($Ref -match '^(.+)@(.+)$') {
        $name = $Matches[1]
        $registry = $Matches[2]
        $registryDir = "$env:USERPROFILE\.conductor\registries\$registry\recursive"
        $candidate = Join-Path $registryDir "$name.yaml"
        if (Test-Path $candidate) { return $candidate }
        $candidate = Join-Path $registryDir "$name.yml"
        if (Test-Path $candidate) { return $candidate }
    }

    Write-Error "Cannot resolve workflow: $Ref"
    exit 1
}

# ── Parse YAML (lightweight — extracts MCP servers, agents, prompts) ───
function Parse-WorkflowYaml {
    param([string]$Path)

    $content = Get-Content $Path -Raw
    $dir = Split-Path $Path -Parent

    # Extract MCP servers
    $mcpServers = @()
    $inMcp = $false
    $inServer = $false
    $currentServer = $null
    $indent = 0

    foreach ($line in (Get-Content $Path)) {
        if ($line -match '^\s+mcp_servers:\s*$') {
            $inMcp = $true
            $indent = ($line -replace '[^\s].*', '').Length
            continue
        }
        if ($inMcp) {
            $lineIndent = if ($line -match '^\s+') { ($line -replace '[^\s].*', '').Length } else { 0 }
            if ($lineIndent -le $indent -and $line.Trim() -ne '') { $inMcp = $false; continue }

            if ($line -match '^\s{' + ($indent + 2) + ',' + ($indent + 6) + '}(\w[\w-]*):\s*$') {
                if ($currentServer) { $mcpServers += $currentServer }
                $currentServer = @{ Name = $Matches[1]; Command = ""; Args = @(); Type = "stdio" }
            }
            elseif ($currentServer -and $line -match '^\s+command:\s*(.+)') {
                $currentServer.Command = $Matches[1].Trim()
            }
            elseif ($currentServer -and $line -match '^\s+type:\s*(.+)') {
                $currentServer.Type = $Matches[1].Trim()
            }
            elseif ($currentServer -and $line -match '^\s+args:') {
                # Collect args on following lines
            }
            elseif ($currentServer -and $line -match '^\s+-\s*"?([^"]+)"?\s*$') {
                $currentServer.Args += $Matches[1]
            }
        }
    }
    if ($currentServer) { $mcpServers += $currentServer }

    # Extract agents with prompts
    $agents = @()
    $inAgents = $false
    $currentAgent = $null

    foreach ($line in (Get-Content $Path)) {
        if ($line -match '^agents:\s*$') { $inAgents = $true; continue }
        if ($inAgents) {
            if ($line -match '^\s+-\s+name:\s*(\S+)') {
                if ($currentAgent) { $agents += $currentAgent }
                $currentAgent = @{
                    Name = $Matches[1]
                    Type = "agent"
                    Model = ""
                    SystemPrompt = ""
                    Prompt = ""
                    SystemPromptFile = ""
                    PromptFile = ""
                }
            }
            elseif ($currentAgent) {
                if ($line -match '^\s+type:\s*(\S+)') { $currentAgent.Type = $Matches[1] }
                if ($line -match '^\s+model:\s*(\S+)') { $currentAgent.Model = $Matches[1] }
                if ($line -match '^\s+system_prompt:\s*!file\s+(.+)') {
                    $promptPath = $Matches[1].Trim()
                    # Resolve relative to workflow's prompts dir
                    $resolved = Join-Path $dir $promptPath
                    if (Test-Path $resolved) { $currentAgent.SystemPromptFile = $resolved }
                }
                if ($line -match '^\s+prompt:\s*!file\s+(.+)') {
                    $promptPath = $Matches[1].Trim()
                    $resolved = Join-Path $dir $promptPath
                    if (Test-Path $resolved) { $currentAgent.PromptFile = $resolved }
                }
            }
        }
    }
    if ($currentAgent) { $agents += $currentAgent }

    return @{
        Path = $Path
        Dir = $dir
        McpServers = $mcpServers
        Agents = $agents
    }
}

# ── Probe an MCP server for tool schemas ───────────────────────────────
function Probe-McpServer {
    param([hashtable]$Server)

    if ($Server.Type -eq 'http') {
        return @{ Name = $Server.Name; Tools = @(); TotalBytes = 0; Error = "HTTP/SSE server (not probed)" }
    }

    $probeScript = Join-Path $env:TEMP "mcp-probe-$([guid]::NewGuid().ToString('N').Substring(0,8)).js"

    $command = $Server.Command
    $args = $Server.Args

    # Build the node probe script
    $spawnCmd = if ($command -eq 'npx') {
        "spawn('npx', $(ConvertTo-Json $args -Compress), { shell: true })"
    }
    elseif ($command -eq 'node') {
        "spawn('node', $(ConvertTo-Json $args -Compress))"
    }
    elseif ($command -eq 'pwsh') {
        "spawn('pwsh', $(ConvertTo-Json $args -Compress))"
    }
    else {
        "spawn('$command', $(ConvertTo-Json $args -Compress))"
    }

    @"
const { spawn } = require('child_process');
const p = $spawnCmd;
let buf = '';
p.stdout.on('data', d => {
    buf += d.toString();
    const lines = buf.split('\n');
    buf = lines.pop();
    for (const line of lines) {
        try {
            const msg = JSON.parse(line);
            if (msg.result && msg.result.tools) {
                const tools = msg.result.tools;
                const result = { tools: tools.map(t => ({ name: t.name, bytes: Buffer.byteLength(JSON.stringify(t)) })), total: 0 };
                result.total = result.tools.reduce((s, t) => s + t.bytes, 0);
                console.log(JSON.stringify(result));
                p.kill();
                process.exit(0);
            }
        } catch(e) {}
    }
});
p.stderr.on('data', () => {});
setTimeout(() => {
    p.stdin.write('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"diag","version":"1.0"}}}\n');
}, 1000);
setTimeout(() => {
    p.stdin.write('{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}\n');
}, 3000);
setTimeout(() => { console.log('{"error":"timeout"}'); p.kill(); process.exit(1); }, 15000);
"@ | Set-Content $probeScript -Encoding utf8

    try {
        $output = node $probeScript 2>$null | Select-Object -First 1
        Remove-Item $probeScript -ErrorAction SilentlyContinue

        if ($output -and $output -match '"tools"') {
            $result = $output | ConvertFrom-Json
            return @{
                Name = $Server.Name
                Tools = $result.tools
                TotalBytes = $result.total
                Error = $null
            }
        }
        elseif ($output -match '"error"') {
            return @{ Name = $Server.Name; Tools = @(); TotalBytes = 0; Error = "Timeout" }
        }
    }
    catch {
        Remove-Item $probeScript -ErrorAction SilentlyContinue
    }

    # Fallback: try direct process spawn for native binaries (twig-mcp, etc.)
    try {
        $psi = [System.Diagnostics.ProcessStartInfo]::new()
        $psi.FileName = $command
        foreach ($a in $args) { $psi.ArgumentList.Add($a) }
        $psi.RedirectStandardInput = $true
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true

        $proc = [System.Diagnostics.Process]::Start($psi)
        $proc.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"diag","version":"1.0"}}}')
        $proc.StandardInput.Flush()
        $initLine = $proc.StandardOutput.ReadLine()

        $proc.StandardInput.WriteLine('{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}')
        $proc.StandardInput.Flush()
        $toolsLine = $proc.StandardOutput.ReadLine()
        try { $proc.Kill() } catch {}

        if ($toolsLine -match '"tools"') {
            $resp = $toolsLine | ConvertFrom-Json
            $tools = $resp.result.tools
            $totalBytes = 0
            $toolList = @()
            foreach ($t in $tools) {
                $json = $t | ConvertTo-Json -Depth 10 -Compress
                $bytes = [System.Text.Encoding]::UTF8.GetByteCount($json)
                $totalBytes += $bytes
                $toolList += @{ name = $t.name; bytes = $bytes }
            }
            return @{ Name = $Server.Name; Tools = $toolList; TotalBytes = $totalBytes; Error = $null }
        }
    }
    catch {}

    return @{ Name = $Server.Name; Tools = @(); TotalBytes = 0; Error = "Failed to probe" }
}

# ── Main ───────────────────────────────────────────────────────────────

$workflowPath = Resolve-WorkflowPath $Workflow
$parsed = Parse-WorkflowYaml $workflowPath

Write-Host ""
Write-Host "╔══════════════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║  Conductor Context Budget Diagnostic                           ║" -ForegroundColor Cyan
Write-Host "╚══════════════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""
Write-Host "Workflow: $workflowPath" -ForegroundColor White
Write-Host "Agents:   $($parsed.Agents.Count)" -ForegroundColor White
Write-Host "MCP Svrs: $($parsed.McpServers.Count)" -ForegroundColor White
Write-Host ""

# ── Probe MCP servers ──────────────────────────────────────────────────
Write-Host "━━━ MCP Server Tool Schemas ━━━" -ForegroundColor Yellow
Write-Host ""

$mcpResults = @{}
$totalMcpBytes = 0

foreach ($server in $parsed.McpServers) {
    if ($SkipMcpProbe) {
        Write-Host ("  {0,-30} (skipped — use live probe for real data)" -f $server.Name) -ForegroundColor DarkGray
        continue
    }

    Write-Host "  Probing $($server.Name)..." -ForegroundColor DarkGray -NoNewline
    $result = Probe-McpServer $server
    $mcpResults[$server.Name] = $result

    if ($result.Error) {
        Write-Host (" {0}" -f $result.Error) -ForegroundColor DarkYellow
    }
    else {
        $kb = [math]::Round($result.TotalBytes / 1024, 1)
        Write-Host ""
        Write-Host ("  {0,-30} {1,3} tools  {2,8:N0} bytes  ({3,5:N1} KB)" -f $result.Name, $result.Tools.Count, $result.TotalBytes, $kb)
        $totalMcpBytes += $result.TotalBytes

        if ($VerbosePreference -eq 'Continue') {
            foreach ($t in ($result.Tools | Sort-Object { $_.bytes } -Descending)) {
                Write-Host ("    {0,-40} {1,6} bytes" -f $t.name, $t.bytes) -ForegroundColor DarkGray
            }
        }
    }
}

$totalMcpKb = [math]::Round($totalMcpBytes / 1024, 1)
Write-Host ""
Write-Host ("  TOTAL MCP SCHEMAS: {0:N0} bytes ({1:N1} KB)" -f $totalMcpBytes, $totalMcpKb) -ForegroundColor White
Write-Host ""

# ── Measure prompt sizes ──────────────────────────────────────────────
Write-Host "━━━ Agent Prompt Sizes ━━━" -ForegroundColor Yellow
Write-Host ""
Write-Host ("  {0,-25} {1,-15} {2,10} {3,10} {4,10}" -f "AGENT", "TYPE", "SYSTEM", "PROMPT", "TOTAL")
Write-Host ("  {0,-25} {1,-15} {2,10} {3,10} {4,10}" -f "-----", "----", "------", "------", "-----")

$totalPromptBytes = 0
foreach ($agent in $parsed.Agents) {
    $sysBytes = 0
    $promptBytes = 0

    if ($agent.SystemPromptFile -and (Test-Path $agent.SystemPromptFile)) {
        $sysBytes = (Get-Item $agent.SystemPromptFile).Length
    }
    if ($agent.PromptFile -and (Test-Path $agent.PromptFile)) {
        $promptBytes = (Get-Item $agent.PromptFile).Length
    }

    $agentTotal = $sysBytes + $promptBytes
    $totalPromptBytes += $agentTotal

    $sysStr = if ($sysBytes -gt 0) { "{0:N1} KB" -f ($sysBytes/1024) } else { "-" }
    $promptStr = if ($promptBytes -gt 0) { "{0:N1} KB" -f ($promptBytes/1024) } else { "-" }
    $totalStr = if ($agentTotal -gt 0) { "{0:N1} KB" -f ($agentTotal/1024) } else { "-" }
    $typeStr = if ($agent.Type -eq 'agent') { $agent.Model } else { $agent.Type }

    Write-Host ("  {0,-25} {1,-15} {2,10} {3,10} {4,10}" -f $agent.Name, $typeStr, $sysStr, $promptStr, $totalStr)
}

Write-Host ""
Write-Host ("  TOTAL PROMPTS: {0:N0} bytes ({1:N1} KB)" -f $totalPromptBytes, ($totalPromptBytes/1024)) -ForegroundColor White
Write-Host ""

# ── Per-agent context budget ──────────────────────────────────────────
Write-Host "━━━ Per-Agent Context Budget (Estimated) ━━━" -ForegroundColor Yellow
Write-Host ""
Write-Host "  Each LLM agent receives: system_prompt + prompt + MCP tool schemas + routed inputs" -ForegroundColor DarkGray
Write-Host ""

$warningThreshold = 30 * 1024  # 30KB
$dangerThreshold = 60 * 1024   # 60KB

foreach ($agent in ($parsed.Agents | Where-Object { $_.Type -ne 'script' -and $_.Type -ne 'workflow' -and $_.Type -ne 'human_gate' })) {
    $sysBytes = 0; $promptBytes = 0
    if ($agent.SystemPromptFile -and (Test-Path $agent.SystemPromptFile)) { $sysBytes = (Get-Item $agent.SystemPromptFile).Length }
    if ($agent.PromptFile -and (Test-Path $agent.PromptFile)) { $promptBytes = (Get-Item $agent.PromptFile).Length }

    $agentPromptTotal = $sysBytes + $promptBytes
    $agentTotal = $agentPromptTotal + $totalMcpBytes

    $color = if ($agentTotal -ge $dangerThreshold) { "Red" }
             elseif ($agentTotal -ge $warningThreshold) { "Yellow" }
             else { "Green" }

    $indicator = if ($agentTotal -ge $dangerThreshold) { "🔴" }
                 elseif ($agentTotal -ge $warningThreshold) { "🟡" }
                 else { "🟢" }

    $model = if ($agent.Model) { $agent.Model } else { "default" }
    Write-Host ("  {0} {1,-25} ({2})" -f $indicator, $agent.Name, $model) -ForegroundColor $color
    Write-Host ("     Prompts: {0,6:N1} KB  |  MCP schemas: {1,6:N1} KB  |  Total: {2,6:N1} KB" -f ($agentPromptTotal/1024), ($totalMcpBytes/1024), ($agentTotal/1024))
    Write-Host ("     Prompt/Schema ratio: {0:P0} prompt, {1:P0} tool schemas" -f ($agentPromptTotal / [math]::Max($agentTotal, 1)), ($totalMcpBytes / [math]::Max($agentTotal, 1)))
    Write-Host ""
}

# ── Summary ───────────────────────────────────────────────────────────
Write-Host "━━━ Summary ━━━" -ForegroundColor Yellow
Write-Host ""
$grandTotal = $totalPromptBytes + $totalMcpBytes
Write-Host ("  Prompt content:     {0,8:N1} KB" -f ($totalPromptBytes/1024))
Write-Host ("  MCP tool schemas:   {0,8:N1} KB" -f ($totalMcpBytes/1024))
Write-Host ("  Grand total:        {0,8:N1} KB  (before conversation turns)" -f ($grandTotal/1024))
Write-Host ""

if ($totalMcpBytes -gt $totalPromptBytes * 3) {
    Write-Host "  ⚠️  MCP schemas are >3× larger than prompts." -ForegroundColor Yellow
    Write-Host "     Your agents are spending more context on tool definitions than instructions." -ForegroundColor Yellow
    Write-Host "     Consider scoping MCP servers per-agent (conductor supports per-agent runtime.mcp_servers)." -ForegroundColor Yellow
    Write-Host ""
}

$highContextAgents = $parsed.Agents | Where-Object {
    $_.Type -ne 'script' -and $_.Type -ne 'workflow' -and $_.Type -ne 'human_gate'
} | Where-Object {
    $sysBytes = 0; $promptBytes = 0
    if ($_.SystemPromptFile -and (Test-Path $_.SystemPromptFile)) { $sysBytes = (Get-Item $_.SystemPromptFile).Length }
    if ($_.PromptFile -and (Test-Path $_.PromptFile)) { $promptBytes = (Get-Item $_.PromptFile).Length }
    ($sysBytes + $promptBytes + $totalMcpBytes) -ge $dangerThreshold
}

if ($highContextAgents) {
    Write-Host "  🔴 High context agents (>60KB pre-conversation):" -ForegroundColor Red
    foreach ($a in $highContextAgents) {
        Write-Host "     - $($a.Name)" -ForegroundColor Red
    }
    Write-Host ""
}

Write-Host "  Legend: 🟢 <30KB  🟡 30-60KB  🔴 >60KB" -ForegroundColor DarkGray
Write-Host ""
