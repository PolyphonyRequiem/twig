# Twig plans

Use the target's bound workspace. A plan can contain multiple operations.

```text
twig plan preview --file <file> -o json
twig plan apply --file <file> --confirm <preview-digest> -o json
twig plan status --file <file> -o json
```

Apply requires `canApply` and the digest for the exact plan. The journal tracks each operation separately; a failed batch can leave a committed prefix. Inspect status after an interruption; do not replay uncertain operations.
