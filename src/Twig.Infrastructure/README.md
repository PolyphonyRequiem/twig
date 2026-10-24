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
an injected `HttpClient` and GraphQL **queries only**. It is not registered by the
composition root: selecting `provider=github` still fails closed. Authentication,
alias resolution, writes, device sign-in and GitHub persistence are not part of
this transport increment.

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
permission. Multi-request reads do not promise an atomic remote snapshot.

## License

MIT.
