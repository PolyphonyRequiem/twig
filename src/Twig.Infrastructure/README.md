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

## License

MIT.
