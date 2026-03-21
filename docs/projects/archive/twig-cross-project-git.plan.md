# Twig Cross-Project Git Support — Implementation Plan

> **Status:** COMPLETE  
> **Revision notes:** EPIC-001 implemented and review issues resolved. All 1,572 tests pass.

---

## Executive Summary

The current Twig architecture assumes a single ADO project for all API calls — work items, iterations, and git. In practice, many organizations track work items in one project (e.g., `Contoso/MyProject`) but host code and pipelines in a different project (e.g., `Contoso/BackendService`). This plan adds `git.project` and `git.repository` configuration keys, implements `AdoGitClient` (the missing `IAdoGitService` implementation) scoped to the git project, and wires it into DI so that `flow done` (PR creation), `flow close` (PR guard), and future git operations target the correct project.

---

## Problem Statement

1. **Single-project assumption:** `AdoRestClient` and `AdoIterationService` are constructed with a single `project` parameter from `config.Project`. All API URLs use `{orgUrl}/{project}/_apis/...`.

2. **Work items ≠ Git repos:** ADO Git REST APIs (`_apis/git/repositories/{repoId}/pullrequests`) must target the *repository's project*, not the backlog project. When these differ, PR creation and PR queries will fail with 404.

3. **No IAdoGitService implementation:** The `IAdoGitService` interface exists but has no concrete implementation. `FlowDoneCommand` and `FlowCloseCommand` accept `IAdoGitService?` as nullable — it's always `null` today.

4. **No repository targeting:** There's no configuration for which git repository to target for PR operations. The ADO Git API requires a repository ID or name.

---

## Design Decisions

| ID | Decision | Rationale |
|---|---|---|
| DD-01 | Add `git.project` config key (defaults to `config.Project`) | Backward-compatible. Users with same-project setups change nothing. Cross-project users set `git.project` to their code project. |
| DD-02 | Add `git.repository` config key (auto-detected from `git remote -v` if not set) | Most users have a single remote. Auto-detection avoids manual config. Explicit config overrides for multi-remote scenarios. |
| DD-03 | `AdoGitClient` is a separate class from `AdoRestClient` | Work item APIs and git APIs target different projects. Separate classes = separate singleton instances with different project values. No refactoring of `AdoRestClient`. |
| DD-04 | Parse ADO remote URL format to extract project and repo | ADO remote URLs follow `https://dev.azure.com/{org}/{project}/_git/{repo}` or `{org}@dev.azure.com:v3/{org}/{project}/{repo}`. Both are parseable. |
| DD-05 | `AdoGitClient` reuses `AdoRestClient.NormalizeOrgUrl()`, `AdoErrorHandler`, and auth infrastructure | Consistent HTTP handling, error classification, and auth across all ADO clients. |

---

## EPIC-001: Cross-Project Configuration & AdoGitClient Implementation

### Overview

Add `git.project` and `git.repository` to `GitConfig`, implement `AdoGitClient` as the concrete `IAdoGitService`, register it in DI, and add auto-detection of project/repo from `git remote -v`.

### Tasks

#### Task 1.1: Add git.project and git.repository to GitConfig

**File:** `src/Twig.Infrastructure/Config/TwigConfiguration.cs`

Add two new properties to `GitConfig`:

```csharp
public sealed class GitConfig
{
    public string BranchTemplate { get; set; } = "feature/{id}-{title}";
    public string BranchPattern { get; set; } = @"(?:^|/)(?<id>\d{3,})(?:-|/|$)";
    public string DefaultTarget { get; set; } = "main";
    public string? Project { get; set; }      // NEW — defaults to null (falls back to root Project)
    public string? Repository { get; set; }   // NEW — defaults to null (auto-detected from git remote)
}
```

Add `SetValue` cases for `git.project` and `git.repository`:

```csharp
case "git.project":
    Git.Project = value;
    return true;
case "git.repository":
    Git.Repository = value;
    return true;
```

Add a helper method to resolve the effective git project:

```csharp
/// <summary>
/// Returns the project to use for git/PR API calls.
/// Falls back to the root <see cref="Project"/> if <see cref="GitConfig.Project"/> is not set.
/// </summary>
public string GetGitProject() => !string.IsNullOrWhiteSpace(Git.Project) ? Git.Project : Project;
```

**Tests:** `tests/Twig.Infrastructure.Tests/Config/TwigConfigurationTests.cs`
- `SetValue_GitProject_SetsValue`
- `SetValue_GitRepository_SetsValue`
- `GetGitProject_ReturnsGitProjectWhenSet`
- `GetGitProject_FallsBackToRootProject`
- Serialization round-trip test with new properties

#### Task 1.2: Add ADO remote URL parser

**File:** `src/Twig.Infrastructure/Ado/AdoRemoteParser.cs` (new)

Static helper that extracts org, project, and repository from ADO git remote URLs:

```csharp
internal static class AdoRemoteParser
{
    /// <summary>
    /// Parses an ADO git remote URL to extract organization, project, and repository name.
    /// Supports HTTPS format: https://dev.azure.com/{org}/{project}/_git/{repo}
    /// Supports SSH format: git@ssh.dev.azure.com:v3/{org}/{project}/{repo}
    /// Returns null if the URL doesn't match ADO patterns.
    /// </summary>
    public static AdoRemoteInfo? Parse(string remoteUrl);
}

internal sealed record AdoRemoteInfo(string Organization, string Project, string Repository);
```

**Tests:** `tests/Twig.Infrastructure.Tests/Ado/AdoRemoteParserTests.cs`
- HTTPS format: `https://dev.azure.com/Contoso/BackendService/_git/twig` → `("Contoso", "BackendService", "twig")`
- SSH format: `git@ssh.dev.azure.com:v3/Contoso/BackendService/twig` → `("Contoso", "BackendService", "twig")`
- Legacy HTTPS format: `https://Contoso.visualstudio.com/BackendService/_git/twig` → `("Contoso", "BackendService", "twig")`
- Non-ADO URL returns null
- URL with spaces/special chars in project/repo

#### Task 1.3: Implement AdoGitClient

**File:** `src/Twig.Infrastructure/Ado/AdoGitClient.cs` (new)

Concrete `IAdoGitService` implementation using ADO Git REST API v7.1. The client is constructed with the **git project** (from `config.GetGitProject()`), not the backlog project.

```csharp
internal sealed class AdoGitClient : IAdoGitService
{
    private readonly HttpClient _http;
    private readonly IAuthenticationProvider _authProvider;
    private readonly string _orgUrl;
    private readonly string _project;     // git project (may differ from backlog project)
    private readonly string _repository;  // repo name or ID

    public AdoGitClient(
        HttpClient httpClient,
        IAuthenticationProvider authProvider,
        string orgUrl,
        string project,
        string repository) { ... }

    // GET {org}/{project}/_apis/git/repositories/{repo}/pullrequests?searchCriteria.sourceRefName=refs/heads/{branch}&api-version=7.1
    public async Task<IReadOnlyList<PullRequestInfo>> GetPullRequestsForBranchAsync(string branchName, CancellationToken ct = default);

    // POST {org}/{project}/_apis/git/repositories/{repo}/pullrequests?api-version=7.1
    public async Task<PullRequestInfo> CreatePullRequestAsync(PullRequestCreate request, CancellationToken ct = default);

    // GET {org}/{project}/_apis/git/repositories/{repo}?api-version=7.1
    public async Task<string?> GetRepositoryIdAsync(CancellationToken ct = default);

    // GET {org}/{project}/_apis/projects/{project}?api-version=7.1
    public async Task<string?> GetProjectIdAsync(CancellationToken ct = default);

    // PATCH {org}/{project}/_apis/wit/workitems/{id}?api-version=7.1 (uses backlog project — delegates to IAdoWorkItemService)
    public async Task AddArtifactLinkAsync(int workItemId, string artifactUri, string linkType, int revision, CancellationToken ct = default);
}
```

ADO REST API endpoints used:
- **List PRs:** `GET {orgUrl}/{gitProject}/_apis/git/repositories/{repoName}/pullrequests?searchCriteria.sourceRefName=refs/heads/{branch}&searchCriteria.status=active&api-version=7.1`
- **Create PR:** `POST {orgUrl}/{gitProject}/_apis/git/repositories/{repoName}/pullrequests?api-version=7.1` with body `{ sourceRefName, targetRefName, title, description, workItemRefs: [{ id }] }`
- **Get Repo:** `GET {orgUrl}/{gitProject}/_apis/git/repositories/{repoName}?api-version=7.1`

**DTO types** (add to `src/Twig.Infrastructure/Ado/Dtos/` or inline):

```csharp
internal sealed class AdoPullRequestResponse { ... }
internal sealed class AdoPullRequestListResponse { List<AdoPullRequestResponse> Value { get; set; } }
internal sealed class AdoRepositoryResponse { string Id { get; set; } string Name { get; set; } ... }
```

Add DTOs to `TwigJsonContext` for AOT serialization.

**Tests:** `tests/Twig.Infrastructure.Tests/Ado/AdoGitClientTests.cs`
- Mock HTTP responses for PR list, PR create, repo info
- Verify correct URL construction with git project (not backlog project)
- Verify auth header is applied
- Verify error handling (404, 401, offline)

#### Task 1.4: Wire IAdoGitService into DI

**File:** `src/Twig/Program.cs`

Register `IAdoGitService` as a singleton using `config.GetGitProject()` and resolved repository name:

```csharp
// IAdoGitService — uses git-specific project (may differ from backlog project)
services.AddSingleton<IAdoGitService>(sp =>
{
    var cfg = sp.GetRequiredService<TwigConfiguration>();
    var gitProject = cfg.GetGitProject();
    
    // Repository: explicit config > auto-detect from git remote > null (skip registration)
    var repository = cfg.Git.Repository;
    if (string.IsNullOrWhiteSpace(repository))
    {
        // Try auto-detect from git remote
        var gitService = sp.GetService<IGitService>();
        if (gitService is not null)
        {
            try
            {
                var remoteUrl = gitService.GetRemoteUrlAsync("origin").GetAwaiter().GetResult();
                if (remoteUrl is not null)
                {
                    var parsed = AdoRemoteParser.Parse(remoteUrl);
                    if (parsed is not null)
                    {
                        repository = parsed.Repository;
                        // Also use parsed project if git.project not explicitly configured
                        if (string.IsNullOrWhiteSpace(cfg.Git.Project))
                            gitProject = parsed.Project;
                    }
                }
            }
            catch { /* git operations are best-effort */ }
        }
    }
    
    if (string.IsNullOrWhiteSpace(gitProject) || string.IsNullOrWhiteSpace(repository))
        return null!; // Service won't resolve — FlowDoneCommand/FlowCloseCommand handle null gracefully
    
    return new AdoGitClient(
        sp.GetRequiredService<HttpClient>(),
        sp.GetRequiredService<IAuthenticationProvider>(),
        cfg.Organization,
        gitProject,
        repository);
});
```

**Note:** Since `FlowDoneCommand` and `FlowCloseCommand` already accept `IAdoGitService?` as nullable, the null case is handled. If git project or repository can't be resolved, the service is not registered and flow commands skip PR operations gracefully.

#### Task 1.5: Add GetRemoteUrlAsync to IGitService

**File:** `src/Twig.Domain/Interfaces/IGitService.cs`

Add method to the existing interface:

```csharp
Task<string?> GetRemoteUrlAsync(string remoteName, CancellationToken ct = default);
```

**File:** `src/Twig.Infrastructure/Ado/GitCliService.cs` (or wherever the implementation lives)

Implement via `git remote get-url {remoteName}`.

**Tests:** Verify `GetRemoteUrlAsync("origin")` returns the remote URL, returns null when remote doesn't exist.

#### Task 1.6: Update twig init to accept --git-project

**File:** `src/Twig/Commands/InitCommand.cs`

Add optional `--git-project` parameter. When provided, sets `config.Git.Project`. Print a diagnostic message showing the resolved git project and repository during init.

**File:** `src/Twig/Program.cs`

Wire the new parameter through the `init` command route.

---

### Acceptance Criteria

- [x] `twig config set git.project BackendService` sets `Git.Project` in config JSON
- [x] `twig config set git.repository my-repo` sets `Git.Repository` in config JSON  
- [x] `twig config get git.project` returns the configured value
- [x] When `git.project` is not set, `GetGitProject()` returns root `Project`
- [x] `AdoGitClient` constructs API URLs using `git.project`, not root `project`
- [x] `AdoRemoteParser` correctly parses HTTPS, SSH, and legacy ADO remote URLs
- [x] Auto-detection from `git remote -v` populates project and repository when config is empty
- [x] `twig flow-done` creates PRs in the correct git project
- [x] `twig flow-close` queries PRs in the correct git project
- [x] Existing single-project users experience zero behavior change
- [x] All existing tests pass without modification
- [x] New tests cover cross-project config, URL parsing, and API URL construction

---

## File Inventory

### New Files

| File | Purpose |
|---|---|
| `src/Twig.Infrastructure/Ado/AdoGitClient.cs` | `IAdoGitService` implementation targeting git project |
| `src/Twig.Infrastructure/Ado/AdoRemoteParser.cs` | Parse ADO remote URLs to extract org/project/repo |
| `src/Twig.Infrastructure/Ado/Dtos/AdoPullRequestDtos.cs` | PR and repository DTOs for AOT serialization |
| `tests/Twig.Infrastructure.Tests/Ado/AdoGitClientTests.cs` | Unit tests for AdoGitClient |
| `tests/Twig.Infrastructure.Tests/Ado/AdoRemoteParserTests.cs` | Unit tests for remote URL parsing |

### Modified Files

| File | Changes |
|---|---|
| `src/Twig.Infrastructure/Config/TwigConfiguration.cs` | Add `GitConfig.Project`, `GitConfig.Repository`, `GetGitProject()`, `SetValue` cases |
| `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | Add PR and repository DTOs to JSON context |
| `src/Twig/Program.cs` | Register `IAdoGitService` singleton with git project resolution |
| `src/Twig.Domain/Interfaces/IGitService.cs` | Add `GetRemoteUrlAsync()` method |
| `src/Twig/Commands/InitCommand.cs` | Add `--git-project` parameter |
| `tests/Twig.Infrastructure.Tests/Config/TwigConfigurationTests.cs` | New test cases for git.project, git.repository |

---

## Risk Assessment

| Risk | Impact | Mitigation |
|---|---|---|
| ADO remote URL format varies across org configurations | Auto-detection may fail for unusual setups | Explicit `git.project` and `git.repository` config overrides are always available |
| `null!` return from DI factory for IAdoGitService | Possible NRE if future code assumes non-null | FlowDoneCommand/FlowCloseCommand already handle nullable; document pattern |
| AOT serialization of new DTOs | Missing type in JsonContext = runtime crash | Add all DTOs to TwigJsonContext; verify with AOT smoke tests |
