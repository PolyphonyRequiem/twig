# PolyphonyRequiem.Twig.Infrastructure

Native host support for [twig](https://github.com/PolyphonyRequiem/twig), targeting
.NET 10 and .NET 11. The old Domain project and general-purpose application
registration graph are removed, not moved into this library.

The CLI composes the retained Core workspace through its closed native catalog.
`Twig.Core` owns the result/error family, process schema, three-way merge, proposals
and execution authority; the separate store and ADO/GitHub providers implement its
two ports. Infrastructure connects that spine to native host requirements:

- Explicit authentication and credential acquisition, named identities, bindings,
  generation and lease admission. Recorded selection is not fresh authentication.
- Native registry and attachment persistence, original request/observation/receipt
  evidence, and independent unknown-write fences. Existing native evidence and
  credentials are preserved; there is no legacy cache migration or evidence reset.
- Core workspace admission and provider access bound to the actual actor, immutable
  target, exact durable attempt and original request hash.
- Repository-declared mechanical policy data and projection into Core, without an
  old policy evaluator or repair-selection application.
- Native Markdown conversion, explicit self-update, and optional privacy-allowlisted
  telemetry. Automatic companion acquisition and retired UI hosts are absent.

`connection writes` is read-only correlated transport inspection, not settlement or
execution success. A receipt does not replace the Core journal. No inspection path
replays, adopts, rewrites a digest, clears a fence or deletes evidence.

The retained production CLI and real HTTP/SQLite boundary tests qualify the current
composition. Compiled PE regressions verify that CLI and Infrastructure have no
Domain dependency; `tools/verify-domain-cut.py` separately checks source/project
absence. The public, I/O-free rendering boundary and its external .NET 10 consumer
belong to `Twig.RenderTree`, not this library.

## License

MIT.
