# bench commands

Current bundled command behavior. Executable help supplies declaration-derived syntax.

| Command | Behavior |
|---|---|
| [`bench create`](create.md) | Create a Bench with a name you will recognise later. |
| [`bench`](bench.md) | List the Benches that exist, marking the current one. |
| [`bench list`](list.md) | List the Benches that exist, marking the current one. |
| [`bench rename`](bench-rename.md) | Rename a user-wide Bench without changing its pins or context selections. |
| [`bench switch`](switch.md) | Stand on another Bench. Naming one that does not exist fails; nothing is created. |
| [`bench delete`](delete.md) | Delete a Bench. One holding pins reports what it holds and deletes nothing. |
| [`bench rules`](bench-rules.md) | List the exact shared Bench pins/rules and captured revision; no provider reads. |
| [`bench rule add`](bench-rule-add.md) | Add one connection-qualified shared rule. Conditions inside it are AND; independent Bench pins/rules are OR. |
| [`bench rule remove`](bench-rule-remove.md) | Remove one captured shared pin/rule without deleting any upstream item or local draft. |
| [`bench planning`](bench-planning.md) | Read cached native planning metadata without refreshing it or changing any Bench. |
| [`bench planning show`](bench-planning-show.md) | Read the current context's cached native planning metadata; alias of bench planning. |
| [`bench planning refresh`](bench-planning-refresh.md) | Explicitly refresh native areas and the exact selected team's iteration calendar; no work items or Bench rules change. |
| [`bench sources`](bench-sources.md) | Discover native saved-query hierarchy and exact-team backlogs; does not execute them or change selection. |
| [`bench browse`](bench-browse.md) | Explicitly acquire saved-query/backlog membership and item bodies into the native cache; never pins, selects or publishes. |
