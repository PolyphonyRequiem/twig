---
id: 0003
title: Seed identity model
type: grilling
status: open
blocked_by: [0001]
---

## Question

Should a seed's identity remain a recycled negative integer, or become a durable `StagedIdentity`? Issue #280 proves the current model is broken: `SeedPublishOrchestrator.cs:265-266` deletes the published seed row, `SqliteWorkItemRepository.cs:282` re-seeds the counter from `MIN(id) FROM work_items WHERE is_seed=1`, so `SeedIdCounter.cs:14` re-issues IDs that `publish_id_map` still treats as permanent keys. Three options: (a) durable high-water mark for the allocator, (b) seed the counter from both tables, (c) `StagedIdentity` value object, leaving the negative int as a display concern. (a) is smallest; (c) makes the bug class impossible. This decision also governs the #268/#269/#270 family, so decide it once rather than patching twice.

## Answer

<!-- empty until resolved -->
