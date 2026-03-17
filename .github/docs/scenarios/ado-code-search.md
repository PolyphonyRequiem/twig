# Azure DevOps Code Search Scenario

## Overview

The Azure DevOps Code Search scenario provides powerful search and discovery capabilities across Azure DevOps repositories. It uses PowerShell-based skills to perform code searches, find patterns, and read file contents from ADO repositories across your organization's codebases.

## What's Included

### Agents
- **ADOCodeSearcher** - Expert Azure DevOps code search agent that helps discover code, patterns, and implementations using local skills

### Skills
- **ado-code-search** - Search for code across Azure DevOps repositories using the ALM Search API
- **ado-file-read** - Read file content from Azure DevOps repositories using the Git Items API

## Prerequisites

- Azure DevOps organization with repositories
- Azure CLI installed and authenticated (`az login`), OR
- Azure DevOps Personal Access Token (PAT) with Code (read) permissions

## Authentication

The skills support two authentication methods:

### Option 1: Azure CLI (Recommended)
```bash
az login
```
The skills will automatically use your Azure CLI credentials.

### Option 2: Personal Access Token
Set the environment variable or pass directly to the script:
```powershell
$env:AZURE_DEVOPS_PAT = "your-pat-token"
```

## Example Workflows

### Search for Code

1. Open GitHub Copilot Chat
2. Type: `@ADOCodeSearcher Search for implementations of IResourceProvider in the myorg organization`
3. The agent will:
   - Execute the `Search-AdoCode.ps1` skill
   - Return relevant code snippets
   - Provide file locations and context

### Find Implementations

1. Type: `@ADOCodeSearcher Find all implementations of the retry pattern`
2. The agent will search for:
   - Classes implementing retry interfaces
   - Methods with retry logic
   - Configuration for retry policies

### Read Full File Content

1. Type: `@ADOCodeSearcher Show me the full file content for the first search result`
2. The agent will:
   - Use the `Get-AdoFileContent.ps1` skill
   - Retrieve the complete file or specified line range
   - Display the code with proper formatting

### Discover Patterns

1. Type: `@ADOCodeSearcher What logging patterns are used in this project?`
2. The agent will analyze and report on:
   - Logging frameworks used
   - Common logging patterns
   - Configuration approaches

### Cross-Repository Search

1. Type: `@ADOCodeSearcher Find all usages of ServiceBus across all repositories in the myorg organization`
2. The agent will search across the organization and provide:
   - Repository locations
   - Usage patterns
   - Integration examples

## Use Cases

- **Code Discovery**: Find existing implementations before writing new code
- **Pattern Learning**: Understand how patterns are implemented in your organization
- **Dependency Analysis**: Find all usages of a specific library or service
- **Onboarding**: Help new team members discover existing code and patterns
- **Refactoring**: Find all places that need updates when making breaking changes
- **Best Practices**: Discover how common problems are solved across teams

## Search Tips

### Effective Search Queries

| Goal | Query Example |
|------|---------------|
| Find interface implementations | `class.*implements IResourceProvider` |
| Find method usages | `\.RetryAsync\(` |
| Find configuration files | `appsettings.json ServiceBus` |
| Find error handling | `catch.*Exception` |
| Find async patterns | `async Task` |

### Search Operators

- Use regex patterns for flexible matching
- Combine keywords for more specific results
- Filter by file extension when searching for specific file types

## Difficulty

**Beginner** - Simple to use, requires minimal setup beyond MCP configuration

## Tags

`azure-devops` `code-search` `discovery` `cross-repo` `ado`
