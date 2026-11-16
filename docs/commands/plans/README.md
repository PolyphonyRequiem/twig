# plans commands

Current bundled command behavior. Executable help supplies declaration-derived syntax.

| Command | Behavior |
|---|---|
| [`proposal validate`](proposal-validate.md) | Validate a proposal's canonical file format offline. Preview separately checks application evidence; no mutation. |
| [`proposal preview`](proposal-preview.md) | Preview a proposal with an optional digest guard. No ADO mutation. |
| [`proposal apply`](proposal-apply.md) | Apply a proposal when --confirm &lt;digest&gt; matches the current file digest exactly. Canonical verb is proposal apply; plan apply is a retained deprecated alias. |
| [`proposal status`](proposal-status.md) | Inspect journal states and receipts for an exact proposal digest without applying or settling it; exit 1 when absent. |
| [`pending`](pending.md) | Inspect captured typed draft fields, original baselines and seed relationships in retained draft order. Read-only. |
