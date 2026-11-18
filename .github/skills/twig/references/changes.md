# Twig proposals

Use the target's bound workspace. A proposal can contain multiple operations.

```text
twig proposal preview --file <file> -o json
twig proposal apply --file <file> --confirm <preview-digest> -o json
twig proposal status --file <file> -o json
```

Apply requires `canApply` and the digest for the exact proposal. The journal tracks each operation separately; a failed batch can leave a committed prefix. Inspect status after an interruption; do not replay uncertain operations.
