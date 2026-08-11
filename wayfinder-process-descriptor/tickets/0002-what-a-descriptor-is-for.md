---
id: 0002
title: What a descriptor is for, and what it therefore carries
type: grilling
status: open
claimed_by:
blocked_by: [0001]
---

> 🔴 **SUPERSEDED — tracked on the board as [#220](https://dev.azure.com/PolyphonyRequiem/Twig/_workitems/edit/220).**
> Do not edit or re-sync this file. Kept for git history only.

## Question

Who is the caller, and what is the smallest descriptor that serves them?

Three uses were named in GitHub #368, and they do not want the same document:

1. **Validate input before creating work** — needs `required`, `defaultValue`, resolved
   picklist values, per-type field scoping. Probably does not need layout.
2. **Explain a process to a person** — needs layout and rules. Probably does not need
   defaults.
3. **Compare two processes** — needs `referenceName` and `customization`/`inherits`, because
   the reporter's process used `Agile_MSCSilver.*` refnames under a differently-named process,
   so name-based matching silently breaks. Probably wants everything, thinly.

Decide whether this is **one document with everything** (matching the detail-projection map's
"carry every fact, let the host drop what it wants" rule) or **selectable slices**. Note the
adjacent map reached the carry-everything answer for a form; a process descriptor may not be
the same shape, and inheriting that rule without arguing it is exactly the failure this ticket
exists to prevent.

Then settle the fields question specifically: what does one field entry carry?

🔴 **The per-type scoping fix is not optional and is not a nice-to-have.** Today's output tells
a caller that 85 fields belong to a type when they belong to the project. That is a correctness
defect, not a depth gap. This ticket decides what replaces it; it does not decide *whether*.

## Answer

<!-- empty until resolved -->
