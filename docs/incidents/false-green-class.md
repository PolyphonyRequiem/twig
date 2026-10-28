# The false-green class (AB#350, AB#352, AB#79, AB#398)

<!-- Moved verbatim from AGENTS.md. Historical diagnostic reference, not current validation instructions. -->

### The false-green class: four cards, one shape

AB#350, AB#352, AB#79 and AB#398 are the same defect wearing four costumes — **a check that
looks green while saying nothing, or a refusal that says nothing true**. Worth naming, because
each one was found only after a caller trusted it:

| Card | Surface | What reported success |
|---|---|---|
| AB#350 | `twig new --field a=1 --field b=2` | exit 0 having written one field of two |
| AB#352 | `tools/run-tests.sh --typo` | no verdict line at all, so the mandated grep came back empty |
| AB#79 | `twig link bogus 5` | top-level usage, exit 0, nothing created |
| AB#398 | `twig note hello world` | `Argument 'world' is not recognized` — true, and never naming the fix |

🔴 **AB#398's own CARD carried the defect it was reporting, and that is the transferable
lesson.** It stated the symptom as *"one bare word is accepted, the SECOND is rejected"* and
listed `twig new "hello world" --type Task` among the working spellings. Measured against the
real binary at the card's own commit, **both were false**: `note`, `new` and `seed chain`
declared their trailing words as `params string[]` *after* the `CancellationToken`, a shape for
which ConsoleAppFramework 5.7.13 emits **no positional slot at all**, so `twig note hello` was
rejected too and the documented workaround did not work either. Every probe behind the card had
used the same input arity, so the report generalised from one shape. **Run the same command at
0, 1, 2 and 3 tokens, quoted and unquoted, and read WHICH token the error names** — `'hello'`
and `'world'` are different diagnoses wearing one error string.

The consequence for the remedy was structural, not cosmetic. The card asked for a hint
(`Did you mean: twig note "hello world"`) and that hint would have pointed at a **second
identical failure** — a false green in a helpful tone. So the fix is two halves, in order:
restore the accepted spelling with a single `[Argument]` slot, *then* make the leftover refusal
honest. `src/Twig/Commands/StrayPositionalGuard.cs` owns the second half and must never be
extended to a command whose quoted form is not actually accepted.

**The card's "do not re-derive this" note needed one word of correction, which is why the
distinction is recorded here.** The reverted attempt used `[Argument] string**[]**` — an
*array* slot, which consumes one token and comma-splits it, moving the error from the first
word to the second without fixing anything. A **scalar** `[Argument] string?` is a different
shape, is what `set`/`show`/`update` already use, and works. A do-not-re-derive note is
evidence about the exact thing that was tried; check whether your variant is that thing.

The blast radius was wider than the three commands named: a sweep of the repo's own `--help`
examples against the generated parser found `edit` and `init` documenting positional spellings
their parsers rejected. `tools/positional-drift.py` is that sweep, kept as a guard —
it is red on the pre-fix tree and green after, so it discriminates. Its first run reported
**four** commands and one was `nav`, where `nav up`/`nav down` are real subcommands rather than
positionals; the tokenizer, not the corpus, was wrong. **A high first-run count from a new
corpus-wide checker indicts the checker.**

🔴 **AB#79's audit found the class is worse than the "usage and exit 0" signature suggests,
and an audit hunting only that signature would have missed the worst instance.** What a
mistyped subcommand did depended on the group's bare handler, and none of the three outcomes
was an error:

* **No bare handler** (`link`, `bench`, `auth`, `ohmyposh`, `workspace sprint`) — printed
  top-level usage, exit 0. The reported shape.
* **Bare handler taking no positional** (`nav`, `workspace`, `area`) — the stray word was
  ignored and the BARE command ran. `twig nav bogus` launched the interactive navigator.
* **Bare handler taking a positional** (`seed`) — the stray word was consumed AS that
  positional. `twig seed bogus` printed no usage whatsoever: it **created a seed titled
  "bogus"** and exited 0. A false green covering a real write, which is strictly worse than a
  false green covering a no-op.

The guard is `src/Twig/Commands/SubcommandGuard.cs`. It decides on **structure** — is the
chain so far a group prefix whose bare form legitimately consumes a value — rather than on a
per-verb list, and the two judgement calls that structure cannot make (`PrefixesTakingPositional`,
`PrefixesWhereSubcommandWins`) are hand-maintained and therefore pinned by reflection guards
that fail the build when code and registry disagree. That pinning is not optional ceremony:
a hand-maintained list is exactly how the whole `bench` group shipped unreachable (ADO
#148-150, 3,072 CLI tests green).

**`twig set 123` must keep working.** `set`, `show`, `state` and `note` are not group prefixes
— no known command begins `"set "` — so they never reach the rejection branch, and
`GroupedHelpTests.IsKnownCommand_FallsBackToTopLevelWhenCompoundUnknown` passes unmodified.

**`twig link --help` must keep exiting 0.** AB#352's lesson applies in reverse here: a usage
error for a request that SUCCEEDED is a false RED, and a false red corrodes an exit code just
as surely as a false green.

## Mutation harnesses

Where a fix's whole value is that its guards are non-vacuous, the mutation run is worth
keeping rather than describing. `tools/ab79-mutants.sh` patches `SubcommandGuard.cs` wrong ten
ways and reports which arms go red **by name**; a `SURVIVED` line means the tests are weaker
than they look.

```bash
tools/ab79-mutants.sh
```

🔴 **A mutant that DOES NOT COMPILE is not a killed mutant.** Three of the first ten came back
`error CS` — dead-code and unreachable-return errors from mutations that were invalid rather
than caught — and the harness reports that as its own outcome instead of banking it as a pass.
Reading a compile error as "the tests caught it" is the same false green the script exists to
measure.

🔴 **Include a mutant that swaps two guards' WORDING while leaving both firing.** Killing a
guard outright is the easy case; the failure AGENTS.md records for `ConflictFixture`, and which
cost two survived mutants on AB#352, is two guards masking each other so the test passes
against a dead one. Assert each guard's distinct message and mutate one into the other's.
