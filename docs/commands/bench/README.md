# `twig bench`

A **Bench** is a named, durable, saved backlog. You name an arrangement of work
once and return to it later; several Benches can exist side by side, and the one
you are currently standing on decides what your workspace view shows. Everything
standing on a Bench sees the same Bench — there are no private pins.

A Bench holds **selectors** (a pin is a selector that matches one item; a query
is a selector that matches a body of work). The store is durable: a Bench and its
selectors survive cache rebuilds, and the default Bench is always present.

The `bench` command group is the shared surface for managing that store — create
a Bench, list what exists, switch which one is current, or delete one you no
longer need. Every subcommand accepts `-o|--output human|json|minimal`; the
format is *declared* by the caller, never sniffed from the terminal, so a
command means the same thing in a pipe as at a prompt.

## Commands

|Command|Summary|Mutates|
|---|---|---|
|[`bench create`](./create.md)|Create a Bench with a name you will recognise later.|local|
|[`bench list`](./list.md)|List the Benches that exist, marking the current one.|none|
|[`bench switch`](./switch.md)|Stand on another Bench.|local|
|[`bench delete`](./delete.md)|Delete a Bench — one holding pins refuses without `--confirm`.|local|

## See also

- [`workspace`](../workspace/README.md) — the view that a Bench shapes.
