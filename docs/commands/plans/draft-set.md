# `twig draft set`

Stage one exact field value in the recoverable local draft for an item:

```text
twig draft set <id> <field-ref> <value> [-o human|json|minimal]
```

The command reads the item and edit capability through native browser admission,
then saves to the shared SQLite draft owner under the qualified checkout,
connection, binding and item identity. Only a field present in the native form,
carried by Twig and declared persistable can be staged. Quote values containing
spaces. `--output json` returns the stable key, baseline and observed revisions,
generation, exact field effects and conflict state; `minimal` returns a
tab-separated summary.

The draft is local recoverable intent only. This command does not authorize,
capture, or apply an edit, and it does not write to the pending-change store.
Existing pending work blocks staging rather than being merged silently. If a
new native observation has moved beyond the saved baseline, the command records
that observation and any visible field conflicts, retains all draft effects,
and refuses the requested edit. It never rebases or retries automatically.

Exit `0` means the local draft was saved (or the requested value was already
present); `1` means native admission, capability, conflict, or CAS refused the
operation; `2` indicates invalid usage or unsupported output format.
