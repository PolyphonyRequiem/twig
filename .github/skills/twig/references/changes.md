# Twig plans

Use the target's bound workspace. A plan can contain multiple operations.

```text
twig plan preview --file <file> -o json
twig plan apply --file <file> -o json
twig plan status --file <file> -o json
```

Preview is optional. Apply executes the plan read from the named file after checking current provider and workspace evidence. The journal tracks each operation separately; a failed batch can leave a committed prefix. Inspect status after an interruption; do not replay uncertain operations.
