# `twig draft show`

Inspect the shared recoverable draft and its latest native conflict state:

```text
twig draft show <id> [-o human|json|minimal]
```

The command resolves the current qualified item identity through native
admission, then loads the draft shared by native surfaces. When no other local
pending work obscures the current values, it records a changed observed revision
and conflicts using generation CAS. It preserves the original baseline and
proposed values. If pending native work exists, it does not infer field
conflicts from overlaid values; it reports the draft and identifies the local
work as a blocker.

Output supports `human`, structured `json`, and a tab-separated `minimal`
summary. `ids` is not supported. A shown or saved draft is not an authorization
or an apply-ready capture.
