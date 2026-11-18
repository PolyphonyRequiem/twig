---
name: twig-omp-presenter
description: Render Twig proposal previews in OMP.
---

- Call `twig_proposal_render` with `file` and optional `workspace` or `full`. Use its returned native preview; do not generate a second one.
- Switch between the retained `brief` and `full` frames. Rendering is read-only; Apply uses the retained digest.
