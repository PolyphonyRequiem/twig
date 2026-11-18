---
name: twig-sdlc
description: Run the Twig SDLC workflow in Conductor.
---

From the intended worktree:

```text
conductor run twig-sdlc-full@twig --input work_item_id=<ID> --input pr_owner=PolyphonyRequiem --input pr_repo_name=twig --web
```

`intent` defaults to `resume`; `new` starts fresh and `redo` deletes existing children and branches.
