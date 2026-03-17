---
name: ado-file-read
description: Read file content from Azure DevOps repositories using the Git Items API. Use this skill when you need to view specific files or specific line ranges from ADO repositories. Similar to local read_file but for remote ADO repos. Supports reading entire files or specific line ranges with 1-based indexing.
---

# Azure DevOps File Read Skill

This skill enables reading file content from Azure DevOps repositories, with support for retrieving specific line ranges. Works like a remote version of the local `read_file` tool.

## When to Use This Skill

Use this skill when you need to:
- Read file content from an Azure DevOps repository without cloning
- View specific line ranges of a remote file
- Fetch code from a specific branch, tag, or commit
- Follow up on code search results to view full file content
- Analyze code in repositories you don't have locally

## How to Read Files

Run the PowerShell script located at `.github/skills/ado-file-read/Get-AdoFileContent.ps1`:

```powershell
.\Get-AdoFileContent.ps1 -RepoUrl <repo-url> -Path <file-path> [-StartLine <n>] [-EndLine <n>] [-Branch <branch>]
```

### Parameters

| Parameter | Required | Type | Description |
|-----------|----------|------|-------------|
| `RepoUrl` | Yes | string | The Azure DevOps repository URL |
| `Path` | Yes | string | File path relative to repository root |
| `StartLine` | No | int | Starting line number (1-based indexing) |
| `EndLine` | No | int | Ending line number (1-based, inclusive) |
| `Branch` | No | string | Branch, tag, or commit SHA to read from |
| `Pat` | No | string | Personal Access Token (if not using Azure CLI) |

### Supported Repository URL Formats

| Format | Example |
|--------|---------|
| Modern | `https://dev.azure.com/{org}/{project}/_git/{repo}` |
| With username | `https://{org}@dev.azure.com/{org}/{project}/_git/{repo}` |
| Legacy | `https://{org}.visualstudio.com/{project}/_git/{repo}` |

## Examples

### Read entire file

```powershell
.\Get-AdoFileContent.ps1 `
    -RepoUrl "https://dev.azure.com/myorg/myproject/_git/myrepo" `
    -Path "src/Program.cs"
```

### Read specific line range (lines 10-50)

```powershell
.\Get-AdoFileContent.ps1 `
    -RepoUrl "https://dev.azure.com/myorg/myproject/_git/myrepo" `
    -Path "src/Program.cs" `
    -StartLine 10 `
    -EndLine 50
```

### Read first 20 lines

```powershell
.\Get-AdoFileContent.ps1 `
    -RepoUrl "https://msazure.visualstudio.com/One/_git/MyRepo" `
    -Path "README.md" `
    -StartLine 1 `
    -EndLine 20
```

### Read from line 100 to end of file

```powershell
.\Get-AdoFileContent.ps1 `
    -RepoUrl "https://dev.azure.com/myorg/myproject/_git/myrepo" `
    -Path "src/Program.cs" `
    -StartLine 100
```

### Read from a specific branch

```powershell
.\Get-AdoFileContent.ps1 `
    -RepoUrl "https://dev.azure.com/myorg/myproject/_git/myrepo" `
    -Path "src/Program.cs" `
    -Branch "feature/my-branch" `
    -StartLine 1 `
    -EndLine 50
```

### Read from a specific commit

```powershell
.\Get-AdoFileContent.ps1 `
    -RepoUrl "https://dev.azure.com/myorg/myproject/_git/myrepo" `
    -Path "src/Program.cs" `
    -Branch "abc123def456789012345678901234567890abcd"
```

## Output Format

The script returns a PowerShell object with:

| Property | Type | Description |
|----------|------|-------------|
| `Path` | string | The requested file path |
| `StartLine` | int | Starting line number returned |
| `EndLine` | int | Ending line number returned |
| `TotalLines` | int | Total lines in the file |
| `Content` | string | File content (filtered to line range) |
| `Lines` | string[] | Array of individual lines |

### Example Output

```
Path       : src/ArmClient/ArmClientFactory.cs
StartLine  : 1
EndLine    : 5
TotalLines : 184
Content    : // --------------------------------------------------------------------------------
             // <copyright file="ArmClientFactory.cs" company="Microsoft Corporation">
             // Copyright (c) Microsoft Corporation. All rights reserved.
             // </copyright>
             // --------------------------------------------------------------------------------
Lines      : {// ----..., // <copyright..., // Copyright..., // </copyright>...}
```

## Authentication

Authentication is resolved in this order:

1. **`-Pat` parameter**: Pass PAT directly
2. **Environment variable**: Set `AZURE_DEVOPS_PAT`
3. **Azure CLI**: Uses `az login` credentials automatically

Required scope: `vso.code` (Code Read)

## Combining with Code Search

Use this skill after `ado-code-search` to view full file content:

1. Search for code using `mcp_ado_search_code`
2. Get the file path from search results
3. Use this skill to read the full content or specific lines

```powershell
# After finding a file via search, read its content
.\Get-AdoFileContent.ps1 `
    -RepoUrl "https://dev.azure.com/myorg/ProjectName/_git/RepoName" `
    -Path "/src/Services/MyService.cs" `
    -StartLine 50 `
    -EndLine 100
```

## Error Handling

| Error | Meaning |
|-------|---------|
| 404 | File not found - verify path and branch |
| 401 | Authentication failed - check credentials |
| 403 | Access denied - check repository permissions |
| Line out of range | StartLine exceeds total line count |

## Best Practices

1. **Specify line ranges for large files**: Reduces data transfer and improves performance
2. **Use Azure CLI auth**: Run `az login` once, then authentication is automatic
3. **Specify branch explicitly**: Ensures reproducible results
4. **Check TotalLines first**: Read without line params to see file size, then fetch specific ranges
