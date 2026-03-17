<#
.SYNOPSIS
    Retrieves specific lines from a file in an Azure DevOps repository using the REST API.

.DESCRIPTION
    This script fetches file content from an Azure DevOps repository and filters the output
    to return only the specified line range. Uses the Azure DevOps Items API to retrieve
    the raw file content.

.PARAMETER RepoUrl
    The Azure DevOps repository URL. Supports formats:
    - https://dev.azure.com/{org}/{project}/_git/{repo}
    - https://{org}.visualstudio.com/{project}/_git/{repo}
    - https://{org}@dev.azure.com/{org}/{project}/_git/{repo}

.PARAMETER Path
    The path of the file to read relative to the root of the repository.
    Example: "src/MyClass.cs" or "/src/MyClass.cs"

.PARAMETER StartLine
    The starting line number to read from (1-based indexing). Optional.
    If not specified, reads from the beginning of the file.

.PARAMETER EndLine
    The ending line number to read to (1-based, inclusive). Optional.
    If not specified, reads to the end of the file.

.PARAMETER Branch
    The branch, tag, or commit to read from. Optional.
    Defaults to the repository's default branch.

.PARAMETER Pat
    Personal Access Token for authentication. Optional.
    If not provided, will attempt to use Azure CLI authentication or the AZURE_DEVOPS_PAT environment variable.

.EXAMPLE
    # Read entire file
    .\Get-AdoFileContent.ps1 -RepoUrl "https://dev.azure.com/myorg/myproject/_git/myrepo" -Path "src/Program.cs"

.EXAMPLE
    # Read lines 10-50 from a file
    .\Get-AdoFileContent.ps1 -RepoUrl "https://dev.azure.com/myorg/myproject/_git/myrepo" -Path "src/Program.cs" -StartLine 10 -EndLine 50

.EXAMPLE
    # Read from line 100 to end of file
    .\Get-AdoFileContent.ps1 -RepoUrl "https://dev.azure.com/myorg/myproject/_git/myrepo" -Path "src/Program.cs" -StartLine 100

.EXAMPLE
    # Read from a specific branch
    .\Get-AdoFileContent.ps1 -RepoUrl "https://dev.azure.com/myorg/myproject/_git/myrepo" -Path "src/Program.cs" -Branch "feature/my-branch" -StartLine 1 -EndLine 20

.OUTPUTS
    PSCustomObject with the following properties:
    - Path: The file path
    - StartLine: The starting line number returned
    - EndLine: The ending line number returned
    - TotalLines: Total lines in the file
    - Content: The file content (filtered to the requested line range)
    - Lines: Array of lines (filtered to the requested line range)

.NOTES
    Requires Azure DevOps REST API access. Authentication can be provided via:
    1. -Pat parameter
    2. AZURE_DEVOPS_PAT environment variable
    3. Azure CLI (az login) - will be used automatically if available

    API Reference: https://learn.microsoft.com/en-us/rest/api/azure/devops/git/items/get
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, HelpMessage = "The Azure DevOps repository URL")]
    [ValidateNotNullOrEmpty()]
    [string]$RepoUrl,

    [Parameter(Mandatory = $true, HelpMessage = "The path of the file relative to repository root")]
    [ValidateNotNullOrEmpty()]
    [string]$Path,

    [Parameter(Mandatory = $false, HelpMessage = "Starting line number (1-based)")]
    [ValidateRange(1, [int]::MaxValue)]
    [int]$StartLine,

    [Parameter(Mandatory = $false, HelpMessage = "Ending line number (1-based, inclusive)")]
    [ValidateRange(1, [int]::MaxValue)]
    [int]$EndLine,

    [Parameter(Mandatory = $false, HelpMessage = "Branch, tag, or commit to read from")]
    [string]$Branch,

    [Parameter(Mandatory = $false, HelpMessage = "Personal Access Token for authentication")]
    [string]$Pat
)

# Strict mode for better error handling
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Parse-AdoRepoUrl {
    <#
    .SYNOPSIS
        Parses an Azure DevOps repository URL and extracts organization, project, and repository names.
    #>
    param([string]$Url)

    # Remove trailing slashes
    $Url = $Url.TrimEnd('/')

    # Pattern 1: https://dev.azure.com/{org}/{project}/_git/{repo}
    if ($Url -match "https://dev\.azure\.com/([^/]+)/([^/]+)/_git/([^/]+)") {
        return @{
            Organization = $Matches[1]
            Project      = $Matches[2]
            Repository   = $Matches[3]
            BaseUrl      = "https://dev.azure.com/$($Matches[1])"
        }
    }

    # Pattern 2: https://{org}@dev.azure.com/{org}/{project}/_git/{repo}
    if ($Url -match "https://[^@]+@dev\.azure\.com/([^/]+)/([^/]+)/_git/([^/]+)") {
        return @{
            Organization = $Matches[1]
            Project      = $Matches[2]
            Repository   = $Matches[3]
            BaseUrl      = "https://dev.azure.com/$($Matches[1])"
        }
    }

    # Pattern 3: https://{org}.visualstudio.com/{project}/_git/{repo}
    if ($Url -match "https://([^.]+)\.visualstudio\.com/([^/]+)/_git/([^/]+)") {
        return @{
            Organization = $Matches[1]
            Project      = $Matches[2]
            Repository   = $Matches[3]
            BaseUrl      = "https://$($Matches[1]).visualstudio.com"
        }
    }

    # Pattern 4: https://{org}.visualstudio.com/DefaultCollection/{project}/_git/{repo}
    if ($Url -match "https://([^.]+)\.visualstudio\.com/DefaultCollection/([^/]+)/_git/([^/]+)") {
        return @{
            Organization = $Matches[1]
            Project      = $Matches[2]
            Repository   = $Matches[3]
            BaseUrl      = "https://$($Matches[1]).visualstudio.com"
        }
    }

    throw "Unable to parse Azure DevOps repository URL: $Url. Expected format: https://dev.azure.com/{org}/{project}/_git/{repo}"
}

function Get-AuthToken {
    <#
    .SYNOPSIS
        Gets authentication token from PAT parameter, environment variable, or Azure CLI.
    #>
    param([string]$PatParam)

    # Priority 1: Direct PAT parameter
    if (-not [string]::IsNullOrEmpty($PatParam)) {
        Write-Verbose "Using PAT from parameter"
        return $PatParam
    }

    # Priority 2: Environment variable
    $envPat = $env:AZURE_DEVOPS_PAT
    if (-not [string]::IsNullOrEmpty($envPat)) {
        Write-Verbose "Using PAT from AZURE_DEVOPS_PAT environment variable"
        return $envPat
    }

    # Priority 3: Azure CLI
    try {
        Write-Verbose "Attempting to get token from Azure CLI..."
        $token = az account get-access-token --resource "499b84ac-1321-427f-aa17-267ca6975798" --query accessToken -o tsv 2>$null
        if (-not [string]::IsNullOrEmpty($token)) {
            Write-Verbose "Using token from Azure CLI"
            return $token
        }
    }
    catch {
        Write-Verbose "Azure CLI token acquisition failed: $_"
    }

    throw "No authentication token available. Please provide a PAT via -Pat parameter, AZURE_DEVOPS_PAT environment variable, or login via Azure CLI (az login)"
}

function Get-AdoFileContent {
    <#
    .SYNOPSIS
        Fetches file content from Azure DevOps repository.
    #>
    param(
        [string]$BaseUrl,
        [string]$Project,
        [string]$Repository,
        [string]$FilePath,
        [string]$Branch,
        [string]$Token
    )

    # Ensure path starts without leading slash for API
    $FilePath = $FilePath.TrimStart('/')

    # Build the API URL
    # API: GET https://dev.azure.com/{organization}/{project}/_apis/git/repositories/{repositoryId}/items?path={path}&api-version=7.1
    $apiUrl = "$BaseUrl/$Project/_apis/git/repositories/$Repository/items"
    
    $queryParams = @{
        "path"           = "/$FilePath"
        "api-version"    = "7.1"
        "includeContent" = "true"
        '$format'        = "text"  # Request raw text content
    }

    if (-not [string]::IsNullOrEmpty($Branch)) {
        # Determine if it's a branch, tag, or commit
        # For simplicity, assume it's a branch if it doesn't look like a SHA
        if ($Branch -match "^[0-9a-f]{40}$") {
            $queryParams["versionDescriptor.version"] = $Branch
            $queryParams["versionDescriptor.versionType"] = "commit"
        }
        else {
            $queryParams["versionDescriptor.version"] = $Branch
            $queryParams["versionDescriptor.versionType"] = "branch"
        }
    }

    $queryString = ($queryParams.GetEnumerator() | ForEach-Object { 
        "$([System.Uri]::EscapeDataString($_.Key))=$([System.Uri]::EscapeDataString($_.Value))" 
    }) -join "&"

    $fullUrl = "$apiUrl`?$queryString"
    Write-Verbose "API URL: $fullUrl"

    # Create authorization header
    # If token looks like a PAT (doesn't contain dots like a JWT), use Basic auth
    if ($Token -notmatch "\..*\.") {
        # PAT - use Basic authentication
        $base64Auth = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes(":$Token"))
        $headers = @{
            "Authorization" = "Basic $base64Auth"
        }
    }
    else {
        # Bearer token (from Azure CLI)
        $headers = @{
            "Authorization" = "Bearer $Token"
        }
    }

    try {
        $response = Invoke-RestMethod -Uri $fullUrl -Headers $headers -Method Get -ContentType "text/plain"
        return $response
    }
    catch {
        $statusCode = $_.Exception.Response.StatusCode.value__
        $errorMessage = $_.ErrorDetails.Message

        switch ($statusCode) {
            404 { throw "File not found: $FilePath in repository $Repository. Verify the path and branch are correct." }
            401 { throw "Authentication failed. Please check your credentials." }
            403 { throw "Access denied. You may not have permission to access this repository." }
            default { throw "Failed to retrieve file content. Status: $statusCode. Error: $errorMessage" }
        }
    }
}

function Filter-Lines {
    <#
    .SYNOPSIS
        Filters content to return only the specified line range.
    #>
    param(
        [string]$Content,
        [int]$Start,
        [int]$End
    )

    # Split content into lines, preserving empty lines
    $lines = $Content -split "`n"
    $totalLines = $lines.Count

    # Determine effective start and end
    $effectiveStart = if ($Start -gt 0) { $Start } else { 1 }
    $effectiveEnd = if ($End -gt 0) { [Math]::Min($End, $totalLines) } else { $totalLines }

    # Validate line numbers
    if ($effectiveStart -gt $totalLines) {
        throw "StartLine ($effectiveStart) exceeds total line count ($totalLines)"
    }

    if ($effectiveStart -gt $effectiveEnd) {
        throw "StartLine ($effectiveStart) cannot be greater than EndLine ($effectiveEnd)"
    }

    # Convert to 0-based index and extract lines
    $startIndex = $effectiveStart - 1
    $endIndex = $effectiveEnd - 1
    $selectedLines = $lines[$startIndex..$endIndex]

    # Join lines back with newlines
    $filteredContent = $selectedLines -join "`n"

    return @{
        Content    = $filteredContent
        Lines      = $selectedLines
        StartLine  = $effectiveStart
        EndLine    = $effectiveEnd
        TotalLines = $totalLines
    }
}

# Main execution
try {
    # Parse repository URL
    Write-Verbose "Parsing repository URL..."
    $repoInfo = Parse-AdoRepoUrl -Url $RepoUrl

    Write-Verbose "Organization: $($repoInfo.Organization)"
    Write-Verbose "Project: $($repoInfo.Project)"
    Write-Verbose "Repository: $($repoInfo.Repository)"

    # Get authentication token
    $token = Get-AuthToken -PatParam $Pat

    # Fetch file content
    Write-Verbose "Fetching file content..."
    $content = Get-AdoFileContent `
        -BaseUrl $repoInfo.BaseUrl `
        -Project $repoInfo.Project `
        -Repository $repoInfo.Repository `
        -FilePath $Path `
        -Branch $Branch `
        -Token $token

    # Filter to requested lines
    Write-Verbose "Filtering content to requested line range..."
    $filtered = Filter-Lines -Content $content -Start $StartLine -End $EndLine

    # Return result object
    $result = [PSCustomObject]@{
        Path       = $Path
        StartLine  = $filtered.StartLine
        EndLine    = $filtered.EndLine
        TotalLines = $filtered.TotalLines
        Content    = $filtered.Content
        Lines      = $filtered.Lines
    }

    Write-Output $result
}
catch {
    Write-Error "Error: $_"
    exit 1
}
