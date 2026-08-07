---
id: 0001
title: Locate the public package boundary and reusable existing contract
type: research
status: open
claimed_by:
blocked_by: []
---

## Question

Which existing Twig assemblies and types can own the public detail projection without importing Infrastructure or a UI framework, and what must change versus remain internal? Ground the answer in project references, package metadata, public API manifests, `FormLayout`, `IFormLayoutProvider`, `ProcessLayoutCommand`, `WorkItem`, appearance types, and current external package conventions.

The answer must include the real consumer construction chain and identify any place where a proposed package boundary would force a consumer to reference Twig.Infrastructure.

## Answer

