<#
.SYNOPSIS
    Searches for code across Azure DevOps repositories using the ALM Search REST API.

.DESCRIPTION
    This script performs code searches across Azure DevOps repositories using the Code Search API.
    Supports advanced search syntax including code element filters (class:, method:, def:, etc.),
    location filters (proj:, repo:, path:), and boolean operators.

.PARAMETER Organization
    The Azure DevOps organization name.

.PARAMETER SearchText
    The search query text. Supports Azure DevOps search syntax including:
    - Plain text search
    - Code type functions: class:, method:, def:, ref:, comment:, etc.
    - Location filters: proj:, repo:, path:, file:, ext:
    - Boolean operators: AND, OR, NOT
    - Wildcards: * (single segment), ** (multiple segments)
    - Exact phrases: "quoted text"

.PARAMETER Project
    Optional. Filter results to specific project(s). Can be a single project or an array.

.PARAMETER Repository
    Optional. Filter results to specific repository/repositories.

.PARAMETER Path
    Optional. Filter results to specific path(s). Use / for root.

.PARAMETER Branch
    Optional. Filter results to specific branch(es).

.PARAMETER CodeElement
    Optional. Filter by code element type (e.g., "def", "class", "method", "comment").

.PARAMETER Top
    Maximum number of results to return. Default: 25. Maximum: 1000.

.PARAMETER Skip
    Number of results to skip for pagination. Default: 0.

.PARAMETER IncludeFacets
    Include facets (aggregated counts by project, repo, etc.) in results. Default: false.

.PARAMETER IncludeSnippet
    Include code snippets with match highlights in results. Default: true.

.PARAMETER OrderBy
    Field to sort results by. Options: "filename", "path", "lastmodified". Default: relevance.

.PARAMETER SortOrder
    Sort direction. Options: "ASC", "DESC". Default: "ASC".

.PARAMETER Pat
    Personal Access Token for authentication. Optional.
    If not provided, will attempt to use Azure CLI authentication or the AZURE_DEVOPS_PAT environment variable.

.EXAMPLE
    # Simple search
    .\Search-AdoCode.ps1 -Organization "myorg" -SearchText "UserService"

.EXAMPLE
    # Search for class definitions
    .\Search-AdoCode.ps1 -Organization "myorg" -SearchText "class:UserService"

.EXAMPLE
    # Search with project and repository filters
    .\Search-AdoCode.ps1 -Organization "myorg" -SearchText "def:CreateUser" -Project "MyProject" -Repository "MyRepo"

.EXAMPLE
    # Search with multiple filters
    .\Search-AdoCode.ps1 -Organization "myorg" -SearchText "method:*Async" -Project "ApiProject" -Branch "main" -Top 50

.EXAMPLE
    # Search with code element filter
    .\Search-AdoCode.ps1 -Organization "myorg" -SearchText "HttpClient" -CodeElement "def","class"

.OUTPUTS
    PSCustomObject with the following properties:
    - Count: Total number of matching results
    - Results: Array of search result objects
    - Facets: (if IncludeFacets) Aggregated counts by project, repo, etc.

.NOTES
    Requires Azure DevOps REST API access. Authentication can be provided via:
    1. -Pat parameter
    2. AZURE_DEVOPS_PAT environment variable
    3. Azure CLI (az login) - will be used automatically if available

    Required scope: vso.code (Code Read)

    API Reference: https://learn.microsoft.com/en-us/rest/api/azure/devops/search/code-search-results/fetch-code-search-results
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, HelpMessage = "The Azure DevOps organization name")]
    [ValidateNotNullOrEmpty()]
    [string]$Organization,

    [Parameter(Mandatory = $true, HelpMessage = "The search query text")]
    [ValidateNotNullOrEmpty()]
    [string]$SearchText,

    [Parameter(Mandatory = $false, HelpMessage = "Filter by project name(s)")]
    [string[]]$Project,

    [Parameter(Mandatory = $false, HelpMessage = "Filter by repository name(s). Requires -Project to be specified.")]
    [string[]]$Repository,

    [Parameter(Mandatory = $false, HelpMessage = "Filter by path(s)")]
    [string[]]$Path,

    [Parameter(Mandatory = $false, HelpMessage = "Filter by branch name(s)")]
    [string[]]$Branch,

    [Parameter(Mandatory = $false, HelpMessage = "Filter by code element type(s)")]
    [ValidateSet("def", "class", "method", "comment", "prop", "field", "interface", "namespace", "enum", "type", "struct", "ctor")]
    [string[]]$CodeElement,

    [Parameter(Mandatory = $false, HelpMessage = "Maximum number of results to return")]
    [ValidateRange(1, 1000)]
    [int]$Top = 25,

    [Parameter(Mandatory = $false, HelpMessage = "Number of results to skip")]
    [ValidateRange(0, [int]::MaxValue)]
    [int]$Skip = 0,

    [Parameter(Mandatory = $false, HelpMessage = "Include facets in results")]
    [switch]$IncludeFacets,

    [Parameter(Mandatory = $false, HelpMessage = "Include code snippets in results")]
    [bool]$IncludeSnippet = $true,

    [Parameter(Mandatory = $false, HelpMessage = "Field to sort results by")]
    [ValidateSet("filename", "path", "lastmodified", "")]
    [string]$OrderBy,

    [Parameter(Mandatory = $false, HelpMessage = "Sort direction")]
    [ValidateSet("ASC", "DESC")]
    [string]$SortOrder = "ASC",

    [Parameter(Mandatory = $false, HelpMessage = "Personal Access Token for authentication")]
    [string]$Pat
)

# Strict mode for better error handling
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

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

function Build-SearchRequest {
    <#
    .SYNOPSIS
        Builds the search request body for the Code Search API.
    #>
    param(
        [string]$SearchText,
        [string[]]$Project,
        [string[]]$Repository,
        [string[]]$Path,
        [string[]]$Branch,
        [string[]]$CodeElement,
        [int]$Top,
        [int]$Skip,
        [bool]$IncludeFacets,
        [bool]$IncludeSnippet,
        [string]$OrderBy,
        [string]$SortOrder
    )

    $requestBody = @{
        searchText     = $SearchText
        '$skip'        = $Skip
        '$top'         = $Top
        includeFacets  = $IncludeFacets
        includeSnippet = $IncludeSnippet
    }

    # Build filters object
    $filters = @{}

    if ($Project -and $Project.Count -gt 0) {
        $filters["Project"] = @($Project)
    }

    if ($Repository -and $Repository.Count -gt 0) {
        $filters["Repository"] = @($Repository)
    }

    if ($Path -and $Path.Count -gt 0) {
        $filters["Path"] = @($Path)
    }

    if ($Branch -and $Branch.Count -gt 0) {
        $filters["Branch"] = @($Branch)
    }

    if ($CodeElement -and $CodeElement.Count -gt 0) {
        $filters["CodeElement"] = @($CodeElement)
    }

    if ($filters.Count -gt 0) {
        $requestBody["filters"] = $filters
    }

    # Add sorting if specified
    if (-not [string]::IsNullOrEmpty($OrderBy)) {
        $requestBody['$orderBy'] = @(
            @{
                field     = $OrderBy
                sortOrder = $SortOrder
            }
        )
    }

    return $requestBody
}

function Invoke-CodeSearch {
    <#
    .SYNOPSIS
        Calls the Azure DevOps Code Search API.
    #>
    param(
        [string]$Organization,
        [hashtable]$RequestBody,
        [string]$Token,
        [string]$ProjectScope
    )

    # Build the API URL
    # API: POST https://almsearch.dev.azure.com/{organization}/{project}/_apis/search/codesearchresults?api-version=7.1
    # Note: project is optional in the URL - if omitted, searches across all projects
    $baseUrl = "https://almsearch.dev.azure.com/$Organization"
    
    if (-not [string]::IsNullOrEmpty($ProjectScope)) {
        $apiUrl = "$baseUrl/$ProjectScope/_apis/search/codesearchresults?api-version=7.1"
    }
    else {
        $apiUrl = "$baseUrl/_apis/search/codesearchresults?api-version=7.1"
    }

    Write-Verbose "API URL: $apiUrl"

    # Create authorization header
    # If token looks like a PAT (doesn't contain dots like a JWT), use Basic auth
    if ($Token -notmatch "\..*\.") {
        # PAT - use Basic authentication
        $base64Auth = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes(":$Token"))
        $headers = @{
            "Authorization" = "Basic $base64Auth"
            "Content-Type"  = "application/json"
        }
    }
    else {
        # Bearer token (from Azure CLI)
        $headers = @{
            "Authorization" = "Bearer $Token"
            "Content-Type"  = "application/json"
        }
    }

    $jsonBody = $RequestBody | ConvertTo-Json -Depth 10
    Write-Verbose "Request Body: $jsonBody"

    try {
        $response = Invoke-RestMethod -Uri $apiUrl -Headers $headers -Method Post -Body $jsonBody
        return $response
    }
    catch {
        $statusCode = $null
        $errorMessage = $_.Exception.Message

        if ($_.Exception.Response) {
            $statusCode = [int]$_.Exception.Response.StatusCode
        }
        if ($_.ErrorDetails.Message) {
            $errorMessage = $_.ErrorDetails.Message
        }

        switch ($statusCode) {
            400 { throw "Bad request. Check your search syntax. Error: $errorMessage" }
            401 { throw "Authentication failed. Please check your credentials." }
            403 { throw "Access denied. You may not have permission to search this organization." }
            404 { throw "Organization or project not found: $Organization" }
            default { throw "Code search failed. Status: $statusCode. Error: $errorMessage" }
        }
    }
}

function Format-SearchResults {
    <#
    .SYNOPSIS
        Formats the search results for better readability.
    #>
    param(
        [object]$Response
    )

    $results = @()

    foreach ($item in $Response.results) {
        $result = [PSCustomObject]@{
            FileName   = $item.fileName
            Path       = $item.path
            Project    = $item.project.name
            Repository = $item.repository.name
            Branch     = if ($item.versions -and $item.versions.Count -gt 0) { $item.versions[0].branchName } else { $null }
            ContentId  = $item.contentId
            RepoUrl    = "https://dev.azure.com/$Organization/$($item.project.name)/_git/$($item.repository.name)"
            FileUrl    = "https://dev.azure.com/$Organization/$($item.project.name)/_git/$($item.repository.name)?path=$($item.path)"
            Matches    = $item.matches
        }
        $results += $result
    }

    $output = [PSCustomObject]@{
        Count   = $Response.count
        Results = $results
    }

    if ($Response.facets) {
        $output | Add-Member -NotePropertyName "Facets" -NotePropertyValue $Response.facets
    }

    if ($Response.infoCode -and $Response.infoCode -ne 0) {
        $infoMessage = Get-InfoCodeMessage -InfoCode $Response.infoCode
        $output | Add-Member -NotePropertyName "InfoCode" -NotePropertyValue $Response.infoCode
        $output | Add-Member -NotePropertyName "InfoMessage" -NotePropertyValue $infoMessage
    }

    return $output
}

function Get-InfoCodeMessage {
    <#
    .SYNOPSIS
        Converts API info codes to human-readable messages.
    #>
    param([int]$InfoCode)

    $messages = @{
        0  = "Ok"
        1  = "Account is being reindexed"
        2  = "Account indexing has not started"
        3  = "Invalid Request"
        4  = "Prefix wildcard query not supported"
        5  = "MultiWords with code facet not supported"
        6  = "Account is being onboarded"
        7  = "Account is being onboarded or reindexed"
        8  = "Top value trimmed to max result allowed"
        9  = "Branches are being indexed"
        10 = "Faceting not enabled"
        11 = "Work items not accessible"
        19 = "Phrase queries with code type filters not supported"
        20 = "Wildcard queries with code type filters not supported"
    }

    if ($messages.ContainsKey($InfoCode)) {
        return $messages[$InfoCode]
    }
    return "Unknown info code: $InfoCode"
}

# Main execution
try {
    Write-Verbose "Starting Azure DevOps Code Search..."
    Write-Verbose "Organization: $Organization"
    Write-Verbose "Search Text: $SearchText"

    # Validate that Repository filter requires Project filter
    if ($Repository -and $Repository.Count -gt 0 -and (-not $Project -or $Project.Count -eq 0)) {
        throw "The -Repository parameter requires the -Project parameter to be specified. Azure DevOps Search API requires a Project filter when filtering by Repository."
    }

    # Get authentication token
    $token = Get-AuthToken -PatParam $Pat

    # Build the search request
    $requestBody = Build-SearchRequest `
        -SearchText $SearchText `
        -Project $Project `
        -Repository $Repository `
        -Path $Path `
        -Branch $Branch `
        -CodeElement $CodeElement `
        -Top $Top `
        -Skip $Skip `
        -IncludeFacets $IncludeFacets.IsPresent `
        -IncludeSnippet $IncludeSnippet `
        -OrderBy $OrderBy `
        -SortOrder $SortOrder

    # Determine project scope for URL (use first project if specified)
    $projectScope = $null
    if ($Project -and $Project.Count -eq 1) {
        $projectScope = $Project[0]
    }

    # Execute the search
    Write-Verbose "Executing code search..."
    $response = Invoke-CodeSearch `
        -Organization $Organization `
        -RequestBody $requestBody `
        -Token $token `
        -ProjectScope $projectScope

    # Format and return results
    $formattedResults = Format-SearchResults -Response $response

    Write-Output $formattedResults
}
catch {
    Write-Error "Error: $_"
    exit 1
}
