---
command: process
group: process
summary: List discovered Core type references and explicit schema gaps.
stability: stable
mutates: none
---

# `twig process`

## Behavior

Acquires the pinned provider's actual Core schema and lists its types.
With a type argument, uses [`process <type>`](process-type.md).
References, states, fields, rules and known gaps come from discovery, never
an assumed ADO template. A native API shape is not proof of runtime permission.

## Effects

Can perform provider/schema reads. No work-item mutation or active selection.

## Failure modes

Unavailable native admission/schema refuses.
Use [process description](process-description.md) for a complete structured
Core projection, not the retired native layout export.
