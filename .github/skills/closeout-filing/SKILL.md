---
name: closeout-filing
description: File Twig closeout findings through Conductor.
---

```text
conductor run closeout-filing@twig --web --input epic_id=<ID> --input observations="<text>" --input 'improvements=["<finding>"]'
```

Pass `--input skip_dedup=true` to disable duplicate detection.
