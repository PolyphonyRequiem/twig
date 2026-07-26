---
id: 0001
title: What is twig for?
type: grilling
status: open
---

## Question

Is twig "a great local tool that happens to be usable by a team", or "a team's shared work-management substrate that happens to run locally"? Everything downstream hangs on this. Today the code says the former: state is a per-workspace SQLite cache, ADO is the source of truth, and `twig init --force` can discard local state without loss. But the stated purpose is work management ACROSS a dev team, and the audit found reconciliation is not a named concept anywhere — 11 scattered sites across 4 assemblies. If the answer is "shared substrate", the local/remote reconciliation module becomes the spine of the architecture and the persistence question becomes dependent on it. If it is "local tool", reconciliation stays a sync detail and the surface seam is the more valuable work.

## Answer

<!-- empty until resolved -->
