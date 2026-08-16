#!/usr/bin/env python3
"""AB#398 drift sweep: documented examples that pass a bare/quoted POSITIONAL,
against commands whose GENERATED parser has no positional slot.

Reads:
  - src/Twig/CommandExamples.cs  : the repo's own documented spellings (the oracle)
  - the emitted ConsoleApp.Builder.g.cs : what the parser actually accepts

A command is REJECTING if its generated Run block has no `argumentPosition` branch.
An example USES a positional if, after the command chain, the next token is not an
option and is not itself the value of a preceding option.

Emits one line per drift. Exit 0 always: this is a report, not a gate.
"""
import re
import sys
import shlex
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
EXAMPLES = ROOT / "src/Twig/CommandExamples.cs"


def find_generated(gen_root: Path) -> Path:
    hits = list(gen_root.rglob("ConsoleApp.Builder.g.cs"))
    if not hits:
        sys.exit(f"no ConsoleApp.Builder.g.cs under {gen_root} — build with "
                 "-p:EmitCompilerGeneratedFiles=true first")
    return hits[0]


def parse_examples(text: str) -> dict[str, list[str]]:
    """key -> list of example invocation strings (command part only)."""
    out: dict[str, list[str]] = {}
    key = None
    for line in text.splitlines():
        m = re.match(r'\s*\["([^"]+)"\]\s*=', line)
        if m:
            key = m.group(1)
            out[key] = []
            continue
        if key is None:
            continue
        m = re.match(r'\s*"(twig .*?)"\s*,\s*$', line)
        if m:
            out[key].append(m.group(1).replace('\\"', '"'))
    return out


def parser_blocks(gen: str) -> dict[str, bool]:
    """instance-method name -> has a positional slot.

    Method names are NOT unique across the generated blocks: `twig init` and
    `ohmyposh init` both emit an `instance.Init(`. A plain dict silently keeps the last
    one, which reported `init` as slotless after it had been fixed. OR the flags together
    so a name is 'has a slot' when ANY block of that name does, and let the caller's
    example-key mapping disambiguate the compound case.
    """
    out: dict[str, bool] = {}
    for block in re.split(r'(?=private async Task RunCommand\d+Async)', gen)[1:]:
        m = re.search(r'instance\.(\w+)\(', block)
        if not m:
            continue
        name = m.group(1)
        out[name] = out.get(name, False) or ('argumentPosition' in block)
    return out


def example_uses_positional(example: str, key: str, known: set[str]) -> str | None:
    """Return the positional token an example passes after the command chain, else None.

    A token that EXTENDS the chain into another known command is a subcommand, not a
    positional: `twig nav up` is `nav up`, not `nav` with the argument "up". Reading it
    as a positional was this sweep's only first-run false positive.
    """
    # Strip the trailing description: examples are "twig cmd args   Description".
    invocation = re.split(r'\s{2,}', example.strip(), maxsplit=1)[0]
    try:
        tokens = shlex.split(invocation)
    except ValueError:
        return None
    if not tokens or tokens[0] != "twig":
        return None
    tokens = tokens[1:]
    chain = key.split()
    if tokens[:len(chain)] != chain:
        return None
    rest = tokens[len(chain):]
    i = 0
    while i < len(rest):
        tok = rest[i]
        if tok.startswith('-'):
            # Assume the following token is its value unless it is another option.
            if '=' not in tok and i + 1 < len(rest) and not rest[i + 1].startswith('-'):
                i += 2
            else:
                i += 1
            continue
        if f"{key} {tok}" in known:
            return None  # subcommand, not a positional
        return tok
    return None


def known_commands(program_cs: str) -> set[str]:
    """The KnownCommands registry in Program.cs — the repo's own list of real verbs."""
    m = re.search(r'KnownCommands\s*\{\s*get;\s*\}\s*=\s*(.*?)\];', program_cs, re.S)
    if not m:
        sys.exit("could not locate KnownCommands in Program.cs")
    return set(re.findall(r'"([^"]+)"', m.group(1)))


def main() -> int:
    gen_root = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("/tmp/gen398")
    gen = find_generated(gen_root).read_text()
    slots = parser_blocks(gen)
    examples = parse_examples(EXAMPLES.read_text())
    known = known_commands((ROOT / "src/Twig/Program.cs").read_text())

    # Map an example key ("seed chain") to its generated method name ("SeedChain").
    def method_for(key: str) -> str | None:
        cand = "".join(p.capitalize() for p in re.split(r'[ -]', key))
        if cand in slots:
            return cand
        for name in slots:
            if name.lower() == cand.lower():
                return name
        return None

    drifts = []
    unmapped = []
    for key, lines in sorted(examples.items()):
        method = method_for(key)
        if method is None:
            # A key we cannot map to a generated block (e.g. a command registered from a
            # separate class). Only report it as a blind spot if its examples actually
            # pass a positional — otherwise it cannot be a member of this defect class.
            risky = [ln for ln in lines
                     if example_uses_positional(ln, key, known) is not None]
            if risky:
                unmapped.append((key, risky))
            continue
        if slots[method]:
            continue  # parser accepts a positional — no drift
        for line in lines:
            pos = example_uses_positional(line, key, known)
            if pos is not None:
                drifts.append((key, method, pos, line))

    print(f"commands with a documented example: {len(examples)}")
    print(f"generated parse blocks:             {len(slots)}")
    print(f"  with positional slot:             {sum(slots.values())}")
    print(f"  WITHOUT positional slot:          {len(slots) - sum(slots.values())}")
    if unmapped:
        print(f"UNMAPPED keys passing a positional  {len(unmapped)} — verify by hand:")
        for key, risky in unmapped:
            for line in risky:
                print(f"      [{key}] {line}")
    else:
        print("unmapped keys passing a positional: 0")
    print()
    if not drifts:
        print("NO DRIFT: every documented positional example has a parser slot.")
        return 0
    print(f"DRIFT — documented example passes a positional the parser REJECTS ({len(drifts)}):")
    seen = set()
    for key, method, pos, line in drifts:
        mark = "  " if key in seen else "* "
        seen.add(key)
        print(f"{mark}[{key}] ({method}) positional={pos!r}")
        print(f"      {line}")
    print()
    print(f"distinct commands affected: {len(seen)} -> {sorted(seen)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
