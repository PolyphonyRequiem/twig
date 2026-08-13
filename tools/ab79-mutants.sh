#!/usr/bin/env bash
# AB#79 mutation harness. Patches SubcommandGuard.cs wrong on purpose and reports which
# test arms go red BY NAME. A mutant that SURVIVES means the tests are weaker than they look.
set -uo pipefail

REPO=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
SRC="$REPO/src/Twig/Commands/SubcommandGuard.cs"
BACKUP=$(mktemp)
cp "$SRC" "$BACKUP"
restore() { cp "$BACKUP" "$SRC"; }
trap restore EXIT

unset DOTNET_ROOT
export PATH=/home/polyphonyrequiem/.dotnet-p5:$PATH

run_mutant() {
    local name="$1"
    local log; log=$(mktemp)
    dotnet test "$REPO/tests/Twig.Cli.Tests/Twig.Cli.Tests.csproj" --nologo \
        --filter "FullyQualifiedName~SubcommandGuardTests|FullyQualifiedName~GroupedHelpTests" \
        > "$log" 2>&1
    local rc=$?
    echo "=================================================================="
    echo "MUTANT: $name"
    if grep -q 'error CS' "$log"; then
        echo "  RESULT: DID NOT COMPILE (mutation invalid, rewrite it)"
    elif [ $rc -eq 0 ]; then
        echo "  RESULT: *** SURVIVED *** — tests are too weak"
    else
        echo "  RESULT: killed. Arms red by name:"
        grep -oP 'Twig\.Cli\.Tests\.\S+?\.\K\w+(?=[( ].*\[FAIL\])' "$log" | sort -u | sed 's/^/    /'
        grep -c 'FAIL\]' "$log" | sed 's/^/    total FAIL lines: /'
    fi
    rm -f "$log"
    restore
}

# M1 — the original bug: never reject an unknown subcommand at all.
python3 - "$SRC" <<'PY'
import sys
p=sys.argv[1]; s=open(p).read()
s=s.replace("PrefixesWhereSubcommandWins.Contains(chain)\n                    || !PrefixesTakingPositional.Contains(chain)",
            "!IsGroupPrefix(chain) && IsGroupPrefix(chain)")
open(p,'w').write(s)
PY
run_mutant "M1: unknown-subcommand branch removed (the original AB#79 bug)"

# M2 — the missing-subcommand guard removed. Tests the two guards do not mask each other.
python3 - "$SRC" <<'PY'
import sys
p=sys.argv[1]; s=open(p).read()
s=s.replace("if (PrefixesWithoutBareHandler.Contains(chain))",
            "if (PrefixesWithoutBareHandler.Contains(chain) && chain.Length < 0)")
open(p,'w').write(s)
PY
run_mutant "M2: missing-subcommand branch removed"

# M3 — swap the two guards' wording. Both branches still fire; only DISTINCT assertions catch it.
python3 - "$SRC" <<'PY'
import sys
p=sys.argv[1]; s=open(p).read()
s=s.replace("""return $"Unknown subcommand: '{args[index]}' is not a '{chain}' command." """.strip(),
            """return $"Missing subcommand: '{chain}' requires one." """.strip())
open(p,'w').write(s)
PY
run_mutant "M3: unknown-subcommand emits the MISSING guard's wording"

# M4 — over-eager: reject positionals too. `twig process Bug` breaks.
python3 - "$SRC" <<'PY'
import sys
p=sys.argv[1]; s=open(p).read()
s=s.replace("""            if (IsGroupPrefix(chain)
                && (PrefixesWhereSubcommandWins.Contains(chain)
                    || !PrefixesTakingPositional.Contains(chain)))""",
"""            if (IsGroupPrefix(chain))""")
open(p,'w').write(s)
PY
run_mutant "M4: PrefixesTakingPositional ignored (process/config break)"

# M5 — seed dropped from PrefixesWhereSubcommandWins: `twig seed bogus` creates a seed again.
python3 - "$SRC" <<'PY'
import sys
p=sys.argv[1]; s=open(p).read()
s=s.replace("""    internal static readonly HashSet<string> PrefixesWhereSubcommandWins =
    [
        "seed",
    ];""",
"""    internal static readonly HashSet<string> PrefixesWhereSubcommandWins = [];""")
s=s.replace("""    internal static readonly HashSet<string> PrefixesTakingPositional =
    [
        "process",""",
"""    internal static readonly HashSet<string> PrefixesTakingPositional =
    [
        "seed",
        "process",""")
open(p,'w').write(s)
PY
run_mutant "M5: seed reclassified as taking a positional (silent seed creation returns)"

# M6 — the --help escape removed: a successful help request becomes a false RED.
python3 - "$SRC" <<'PY'
import sys
p=sys.argv[1]; s=open(p).read()
s=s.replace("""        if (args.Any(a => a is "-h" or "--help"))
            return null;""", "")
open(p,'w').write(s)
PY
run_mutant "M6: --help escape removed (false RED on a successful request)"

# M7 — verb list dropped from the message. Exit code still correct, message useless.
python3 - "$SRC" <<'PY'
import sys
p=sys.argv[1]; s=open(p).read()
s=s.replace('return $"Valid \'{chain}\' subcommands: {string.Join(", ", verbs)}";',
            'return chain.Length < 0 ? "x" : string.Empty;')
open(p,'w').write(s)
PY
run_mutant "M7: valid-verb list dropped from the message"

# M8 — DescribeVerbs stops filtering multi-word tails: offers 'area add' under 'workspace'.
python3 - "$SRC" <<'PY'
import sys
p=sys.argv[1]; s=open(p).read()
s=s.replace("            .Where(rest => !rest.Contains(' '))", "")
open(p,'w').write(s)
PY
run_mutant "M8: DescribeVerbs offers unreachable multi-word completions"

# M9 — unknown TOP-LEVEL commands claimed by this guard: two messages for one condition.
python3 - "$SRC" <<'PY'
import sys
p=sys.argv[1]; s=open(p).read()
s=s.replace("""        if (!IsKnownOrPrefix(chain))
            return null;""",
"""        if (!IsKnownOrPrefix(chain))
            return $"Unknown subcommand: '{chain}' is not a 'twig' command.";""")
open(p,'w').write(s)
PY
run_mutant "M9: unknown top-level command claimed by this guard"

# M10 — always-fail guard. Proves the POSITIVE arms exist and are not vacuous.
python3 - "$SRC" <<'PY'
import sys
p=sys.argv[1]; s=open(p).read()
s=s.replace("""        if (args.Length == 0 || args[0].StartsWith('-'))
            return null;""",
"""        if (args.Length >= 0)
            return "Unknown subcommand: 'x' is not a 'y' command.";""")
open(p,'w').write(s)
PY
run_mutant "M10: always-FAILED guard (positive arms must catch this)"

echo "=================================================================="
echo "Restored. Verifying the tree is back to green:"
dotnet test "$REPO/tests/Twig.Cli.Tests/Twig.Cli.Tests.csproj" --nologo \
    --filter "FullyQualifiedName~SubcommandGuardTests|FullyQualifiedName~GroupedHelpTests" 2>&1 | tail -2
