---
name: ado-code-search
description: Search for code across Azure DevOps repositories using the ALM Search API. Use this skill when you need to find code patterns, class definitions, method implementations, or references in Azure DevOps repos. Supports advanced search syntax with filters for projects, repositories, branches, paths, and code elements like classes, methods, and comments.
---

# Azure DevOps Code Search Skill

This skill enables searching for code across Azure DevOps repositories using the ALM Search REST API.

## When to Use This Skill

Use this skill when you need to:
- Find code patterns or implementations across Azure DevOps repositories
- Search for class, method, or interface definitions
- Locate references to specific symbols
- Discover how APIs or functions are used in the codebase
- Find TODO comments or specific documentation

## How to Search

Run the PowerShell script located at `.github/skills/ado-code-search/Search-AdoCode.ps1`:

```powershell
.\Search-AdoCode.ps1 -Organization <org> -SearchText <query> [-Project <project>] [-Repository <repo>] [-Branch <branch>] [-Top <n>]
```

### Parameters

| Parameter | Required | Type | Description |
|-----------|----------|------|-------------|
| `Organization` | Yes | string | The Azure DevOps organization name |
| `SearchText` | Yes | string | Keywords to search for in code repositories |
| `Project` | No | string[] | Filter by project names |
| `Repository` | No | string[] | Filter by repository names |
| `Path` | No | string[] | Filter by file paths |
| `Branch` | No | string[] | Filter by branch names |
| `CodeElement` | No | string[] | Filter by code element type (def, class, method, comment, etc.) |
| `Top` | No | int | Maximum results to return (default: 25, max: 1000) |
| `Skip` | No | int | Number of results to skip for pagination (default: 0) |
| `IncludeFacets` | No | switch | Include facets in results |
| `IncludeSnippet` | No | bool | Include code snippets in results (default: true) |
| `OrderBy` | No | string | Sort by: filename, path, lastmodified |
| `SortOrder` | No | string | Sort direction: ASC or DESC (default: ASC) |
| `Pat` | No | string | Personal Access Token (if not using Azure CLI) |

## Examples

### Simple text search

```powershell
.\Search-AdoCode.ps1 `
    -Organization "myorg" `
    -SearchText "UserService"
```

### Find class definitions

```powershell
.\Search-AdoCode.ps1 `
    -Organization "myorg" `
    -SearchText "class:UserService"
```

### Find all async methods in a project

```powershell
.\Search-AdoCode.ps1 `
    -Organization "myorg" `
    -SearchText "method:*Async" `
    -Project "ApiProject"
```

### Find references to a symbol

```powershell
.\Search-AdoCode.ps1 `
    -Organization "myorg" `
    -SearchText "ref:ErrorHandler"
```

### Find TODO comments

```powershell
.\Search-AdoCode.ps1 `
    -Organization "myorg" `
    -SearchText "comment:TODO"
```

### Search with multiple filters

```powershell
.\Search-AdoCode.ps1 `
    -Organization "myorg" `
    -SearchText "CreateUser" `
    -Project "WebApi" `
    -Repository "UserService" `
    -Branch "main" `
    -Top 50
```

### Filter by code element type

```powershell
.\Search-AdoCode.ps1 `
    -Organization "myorg" `
    -SearchText "HttpClient" `
    -CodeElement "def","class"
```

### Paginate through results

```powershell
# First page
.\Search-AdoCode.ps1 -Organization "myorg" -SearchText "Logger" -Top 25 -Skip 0

# Second page
.\Search-AdoCode.ps1 -Organization "myorg" -SearchText "Logger" -Top 25 -Skip 25
```

### Get results with facets

```powershell
.\Search-AdoCode.ps1 `
    -Organization "myorg" `
    -SearchText "Exception" `
    -IncludeFacets
```

## Search Text Syntax

### Basic Operators

| Operator | Description | Example |
|----------|-------------|---------|
| Plain text | Simple keyword search | `MyClassName` |
| `*` | Wildcard (matches any characters) | `Queue*` |
| `AND` | Both terms must match | `error AND handler` |
| `OR` | Either term matches | `error OR exception` |
| `NOT` | Exclude term | `error NOT warning` |
| `"..."` | Exact phrase match | `"connection string"` |

### Code Type Functions

Use these functions to search for specific code elements:

| Function | Syntax | Description |
|----------|--------|-------------|
| `class:` | `class:ClassName` | Find class definitions |
| `method:` | `method:MethodName` | Find methods/functions |
| `def:` | `def:SymbolName` | Find definitions |
| `ref:` | `ref:SymbolName` | Find references to a symbol |
| `prop:` | `prop:PropertyName` | Find properties |
| `field:` | `field:FieldName` | Find fields |
| `comment:` | `comment:text` | Search within comments |
| `interface:` | `interface:InterfaceName` | Find interfaces |
| `namespace:` | `namespace:Name` | Find namespaces |
| `enum:` | `enum:EnumName` | Find enumerations |
| `type:` | `type:TypeName` | Find types |
| `struct:` | `struct:StructName` | Find structures |
| `strlit:` | `strlit:text` | Search in string literals |
| `basetype:` | `basetype:TypeName` | Find classes inheriting from type |
| `ctor:` | `ctor:ClassName` | Find constructors |

### Location Filter Functions

Use these directly in the search text:

| Function | Syntax | Description |
|----------|--------|-------------|
| `proj:` | `proj:ProjectName` | Filter by project |
| `repo:` | `repo:RepoName` | Filter by repository |
| `path:` | `path:/folder/subfolder` | Filter by path |
| `file:` | `file:filename*` | Filter by filename pattern |
| `ext:` | `ext:cs` | Filter by file extension |

### Path Wildcards

| Pattern | Description |
|---------|-------------|
| `*` | Matches characters in single path segment |
| `**` | Matches across multiple path segments |

## Output Format

The script returns a PowerShell object with:

| Property | Type | Description |
|----------|------|-------------|
| `Count` | int | Total number of matching results |
| `Results` | array | Array of search result objects |
| `Facets` | object | (if IncludeFacets) Aggregated counts by project, repo, etc. |
| `InfoCode` | int | (if non-zero) API status code |
| `InfoMessage` | string | (if non-zero) Human-readable status message |

### Result Object Properties

Each result in the `Results` array contains:

| Property | Type | Description |
|----------|------|-------------|
| `FileName` | string | Name of the matching file |
| `Path` | string | Full path of the file in the repository |
| `Project` | string | Project name |
| `Repository` | string | Repository name |
| `Branch` | string | Branch name where the file was found |
| `ContentId` | string | Content identifier for the file |
| `RepoUrl` | string | URL to the repository |
| `FileUrl` | string | URL to view the file in Azure DevOps |
| `Matches` | object | Match location information |

### Example Output

```
Count   : 15
Results : {
    FileName   : UserService.cs
    Path       : /src/Services/UserService.cs
    Project    : WebApi
    Repository : UserService
    Branch     : main
    ContentId  : abc123...
    RepoUrl    : https://dev.azure.com/myorg/WebApi/_git/UserService
    FileUrl    : https://dev.azure.com/myorg/WebApi/_git/UserService?path=/src/Services/UserService.cs
    Matches    : @{content=...}
}
```

## Authentication

Authentication is resolved in this order:

1. **`-Pat` parameter**: Pass PAT directly
2. **Environment variable**: Set `AZURE_DEVOPS_PAT`
3. **Azure CLI**: Uses `az login` credentials automatically

Required scope: `vso.code` (Code Read)

## Combining with File Read

Use this skill with `ado-file-read` to view full file content after searching:

1. Search for code using this skill
2. Get the file path and repository from search results
3. Use `ado-file-read` skill to read the full content or specific lines

```powershell
# Step 1: Search for code
$results = .\Search-AdoCode.ps1 -Organization "myorg" -SearchText "class:UserService"

# Step 2: Read the file content
$file = $results.Results[0]
.\.github\skills\ado-file-read\Get-AdoFileContent.ps1 `
    -RepoUrl $file.RepoUrl `
    -Path $file.Path `
    -Branch $file.Branch
```

## Best Practices

1. **Start broad, then narrow**: Begin with a general search and add filters to refine
2. **Use code type functions**: `class:`, `method:`, `def:` are more precise than plain text
3. **Combine with location filters**: Add `proj:`, `repo:`, or `path:` to narrow scope
4. **Use wildcards wisely**: `**` for cross-directory, `*` for single segment
5. **Quote paths with spaces**: `path:"My Folder/Sub Folder"`
6. **Use IncludeFacets**: Set to `true` to understand result distribution
7. **Paginate large results**: Use `Skip` and `Top` for large result sets

## Limitations

- Code search doesn't work for forked repositories
- Phrase queries with code type filters not supported
- Wildcard queries with code type filters not supported
- Only indexed branches are searchable (main/default by default)
- Maximum 1000 results per query

## API Reference

This skill uses the Azure DevOps Code Search REST API:
- [Fetch Code Search Results](https://learn.microsoft.com/en-us/rest/api/azure/devops/search/code-search-results/fetch-code-search-results?view=azure-devops-rest-7.1)
