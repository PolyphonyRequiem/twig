---
id: 0005
title: Define optional editing capabilities without mandatory persistence
type: grilling
status: open
claimed_by:
blocked_by: [0002, 0003]
---

## Question

What explicit capability contract lets an editable host discover allowed edits, validate or propose changes, and hand mutations to a caller-owned sink without requiring `IPendingChangeStore` or Twig.Infrastructure for read-only use?

Resolve capability discovery, field mutability, validation/state transitions, change representation, optimistic concurrency/error reporting, and ownership of persistence. Do not make the projection itself mutable or let a null persistence service become an implicit mode switch.

## Answer

