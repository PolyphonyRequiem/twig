# `twig draft review`

Review the exact field effects of one retained native draft capture by its
canonical digest.

```text
twig draft review <digest>
twig draft review <digest> -o json
twig draft review <digest> -o minimal
```

## Flags

| Flag | Contract |
|---|---|
| `<digest>` | Required lowercase 64-character SHA-256 digest from `twig draft retain`. Missing or malformed is a usage refusal. |
| `-o` | `human` (default), `minimal` or `json`. `ids` is refused. |

## Behavior

REVIEW reads the stored proposal journal and native capture, then validates
them against current local evidence: the draft, the cached item revision,
the absence of any pending changes, and the served-metadata evidence
fingerprint. It reads under a non-deferred (write-reserving) local SQLite
transaction on the shared connection and commits it, so it can contend with
concurrent local writers. The source issues no row inserts, updates or deletes on
this path: it does not read or create a proposal file, alter export state or
journal audit fields, import, authorize or apply. It is classified
`mutates: none` on that basis; it is not a remote read.

Freshness means the captured intent matches available cached native evidence
during validation. It is not a remote observation or a lease. Human output
shows each effect with JSON-escaped nullable baseline and proposed values.
`-o minimal` prints `draft reviewed<TAB><digest><TAB><effect count>`, and
`-o json` returns the closed review envelope with `authorized: false` and
`canApply: false`. The cache and journal are attached databases, so review
does not claim cross-file crash atomicity.

## Exit codes

| Exit | Meaning |
|---|---|
| `0` | Reviewed; envelope `status: "reviewed"`. |
| `1` | Refused: `capture-not-found`, `draft-capture-required` (non-capture digest), `draft-capture-invalid`, `draft-capture-missing-evidence` (legacy capture), `draft-capture-stale`, `capture-unavailable`, provider unsupported, or cancellation. |
| `2` | Usage: missing or non-canonical digest, or an unsupported output format. |

## See also

- [`twig draft retain`](draft-retain.md), [`twig proposal status`](proposal-status.md)
