# PolyphonyRequiem.Twig.Infrastructure

Infrastructure for [twig](https://github.com/PolyphonyRequiem/twig) — a
high-performance .NET CLI for Azure DevOps work-item triage.

This package implements the interfaces declared in
[`PolyphonyRequiem.Twig.Domain`](https://www.nuget.org/packages/PolyphonyRequiem.Twig.Domain):

- SQLite-backed persistence (work items, contexts, field definitions,
  pending changes, navigation history, tracking, seed links, …).
- Azure DevOps REST clients (work items, git, iterations).
- Authentication providers (PAT, MSAL, Azure CLI fallback).
- Markdown → HTML conversion for HTML-typed ADO fields.
- Telemetry client (no-op unless `TWIG_TELEMETRY_ENDPOINT` is set).

## Quick start

```csharp
using Microsoft.Extensions.DependencyInjection;
using Twig.Infrastructure;
using Twig.Infrastructure.Config;

var config = TwigConfiguration.Load(".twig/config");

var services = new ServiceCollection()
    .AddTwigInfrastructure(config);

var provider = services.BuildServiceProvider();
```

`AddTwigInfrastructure` is the supported public composition root. It
registers the core domain services (config, paths, SQLite persistence,
repositories, process configuration, prompt state, telemetry) plus the
network services (auth, HTTP, ADO REST clients, iteration service).

## Experimental GitHub snapshot transport

The internal `GitHubWorkItemProvider` implements `IWorkItemSnapshotReader` using
GraphQL **queries only**. It is registered as `IWorkItemProvider` and
`IWorkItemSnapshotReader` only when the workspace explicitly sets
`provider=github`; an absent setting still means ADO and an unknown provider
still fails closed. This increment registers the provider transport only; it
does not dispatch CLI read commands through it. Writes, write readiness,
device sign-in, alias resolution and GitHub persistence are not part of this
increment.

### Activation (`provider=github`)

- **Scope** comes only from explicit Repo-scoped config — `twig config github.host
  <host>` and `twig config github.repository <owner/name>` — never from the git
  remote. Missing or malformed scope fails closed with `provider-unconfigured:`.
  The GraphQL endpoint is `https://api.github.com/graphql` for `github.com`,
  otherwise `https://<host>/api/graphql`. Snapshots for issues outside the
  configured repository are refused.
- **Credential** comes only from the GitHub CLI: `gh auth token --hostname
  <host>`, run as an argument array with no shell, a 10 s timeout and bounded
  output. The token is held in memory for the process only — never persisted,
  logged or sent to telemetry. If `gh` is missing, not logged in, times out or
  returns no usable token, the read fails closed with
  `GitHubCredentialUnavailableException` ("Run `gh auth login --hostname <host>`")
  and there is no fallback source.
- **Read-only is enforced by Twig, not by the token.** The token's real scope is
  whatever the user granted `gh`. `GitHubReadOnlyAuthHandler` admits only a POST
  to the exact configured HTTPS GraphQL endpoint whose body is exactly one of the
  adapter's fixed read documents (`GitHubIssueQueries`) with the expected
  `id`/`after` variables. Any other verb, destination, document, mutation or
  envelope is refused before the token is requested or the network is touched.
  Automatic redirects are disabled and any 3xx response is refused, so the bearer
  token never follows a response to another origin.
- **Process rules**: under `provider=github`, `IProcessRuleProvider` is the
  explicit `EmptyProcessRuleProvider`; ADO keeps `AdoIterationService`.
- ADO-only services still refuse under `provider=github` with
  `provider-unavailable:`.

`FetchSnapshotAsync` accepts a qualified identity: provider `github`, the
lowercase HTTPS API origin without a trailing slash (for example
`https://api.github.com`), and the case-preserved issue node ID. Issue numbers,
repository names and user logins are not item identities. A custom GraphQL
endpoint may use `/graphql` or `/api/graphql`; its authority supplies the scope.
No local ID or revision is minted, and the read-only revision token is null.

Snapshots contain typed `FieldValues`, not the integer-ID ADO mirror shape:
`System.Title`/`System.State` preserve native text, `System.Description` carries
Markdown or an explicit null, and `System.AssignedTo` carries one user's node ID
and login, or null for no assignees. With multiple assignees that scalar is
absent; `github.assignees` always preserves the full list as source-generated
JSON sorted by node ID (`RawValue`, data type `github.assignees.v1`).
`github.labels`, `github.number`, `github.url` and `github.milestone` preserve
labels, display number, URL and milestone title respectively. These field keys
are a non-persisted snapshot agreement, not a GitHub storage schema.

All label, assignee, sub-issue and dependency connections are paginated.
Relationships use qualified endpoints, parent-to-child and blocking-to-blocked
direction, and deduplicate canonical edges. They are provider observations;
provenance is derived from the active capability descriptor. Mentions never
become Related edges. Reads never touch durable Twig-local relationships.

HTTP failures, GraphQL errors (including partial data), invalid identities and
incomplete pagination fail explicitly instead of returning empty observations.
`ProbeCapabilitiesAsync` performs no requests: it exposes the latest failed
read's evidence (`read-denied`, `read-failed` or `read-incomplete`), without
changing static Native/Unsupported declarations. Before a read, and after a
successful read, availability remains unprobed; a read cannot establish write
permission or write readiness. CLI read-command dispatch through this snapshot
reader is not included. Multi-request reads do not promise an atomic remote
snapshot.

## License

MIT.
