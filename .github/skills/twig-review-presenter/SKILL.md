---
name: twig-review-presenter
description: Render a Twig proposal preview.
---

- Read `twig proposal preview --file <file> -o json`. Render the returned `reviewModel` and retain the workspace and digest.
- Rendering does not apply changes. Apply uses the exact digest from the retained preview.
- The offline Discord adapter is `scripts/render_discord_review.py`.
