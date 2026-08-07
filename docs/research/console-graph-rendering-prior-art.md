# Rendering general graphs as text in a terminal — prior art survey

**Purpose.** twig renders strict parent/child hierarchies today. Work-item relations
(`Related`, `Predecessor`, `Successor`) form a general directed graph: cycles are possible,
a node can be reached by several paths, and `Related` is undirected. This document surveys
what *real, shipping* tools do to draw such graphs in a character grid, so that a
prototyping session can copy proven shapes instead of inventing new ones.

Scope: 5–50 nodes, three edge kinds, 80–200 column terminals.

Bias note: almost every widely-used terminal graph renderer is either (a) a *git commit
DAG* renderer, which exploits a total topological order that general work-item graphs do
not have, or (b) a *tree* renderer that fakes graph-ness with deduplication markers.
True general-graph node-link rendering in a terminal is **rare and mostly toy-scale**.
That fact is itself the most important finding here.

---

## 1. Git commit-graph rendering (lanes / column model)

### 1.1 `git log --graph` — the canonical lane algorithm

Source of truth is `graph.c` in git itself:
<https://github.com/git/git/blob/master/graph.c>
Docs: <https://git-scm.com/docs/git-log>

**Data model.** The renderer keeps two arrays of *columns* (git's own term; "lanes"
colloquially) plus a `mapping` array:

- `struct column { struct commit *commit; unsigned short color; unsigned int is_merge_parent:1; }`
- `graph->columns` — column state **before** the current commit is printed.
- `graph->new_columns` — column state **after** it.
- `graph->mapping` — one entry per output *character* position, `-1` for empty, otherwise
  the target column index that this branch line is collapsing toward. Git's own comment:
  *"an array that tracks the current state of each character in the output line during state
  `GRAPH_COLLAPSING`… this array maps the current column positions to their desired positions."*

**Column assignment rule** (`graph_update_columns`, `graph_insert_into_new_columns`):

1. Swap `columns` ← `new_columns`; the previous "after" state becomes the current "before".
2. Capacity for the next row is at most `num_columns + num_parents`.
3. Walk the existing columns left to right. For each column whose commit **is** the commit
   being printed, record `commit_index = i` and then insert *each interesting parent* into
   `new_columns`.
4. `graph_insert_into_new_columns` first calls `graph_find_new_column_by_commit` — a linear
   scan of `new_columns`. **If the parent already occupies a column, reuse that column
   index; do not allocate a new one.** This is the lane-reuse rule: one column per
   *pending commit*, never two.
5. If the parent is new, it is appended at the right-hand end (`mapping_idx = graph->width`),
   except for the left-skew case where the first parent fuses with its neighbour
   (`mapping_idx = graph->width - 2`).
6. Columns whose commit is not the current one are copied through unchanged (their lane is
   preserved, so a long-lived branch keeps a stable column).
7. A lane is **freed** when its commit is printed and has no interesting parents; the
   `GRAPH_COLLAPSING` state then shifts the right-hand lanes left, one character per output
   row, using `mapping`/`old_mapping` — this is what produces the `|/` and `|_|/` shapes.

**Merges.** `edges_added = num_parents - 1` normally (each extra parent takes a new lane);
for left-skewed merges the first parent fuses into the existing column and `edges_added`
is one less. Git's in-source diagrams (verbatim from `graph.c`):

```
		0)			1)

		| | | *-.		| | *---.
		| |_|/|\ \		| | |\ \ \
		|/| | | | |		| | | | | *
```

`merge_layout` is 0 when the first parent is known to be in a column left of the merge
(the left diagram), 1 otherwise. And for octopus merges:

```
		| | |			| |    \
		| * |			| *---. \
		| |\ \			| |\ \ \ \
		| | | |         	| | | | | |

		num_parents: 2		num_parents: 4
		edges_added: 1		edges_added: 3
```

**Character vocabulary.** Only `* | / \ _ .` — plus `-` in the merge fan-out. `_` is the
horizontal run used when a lane must travel several columns left in one row (`|_|/`).
Everything is ASCII by default; git has no box-drawing mode.

**Crossings.** Git does not minimise crossings. It cannot: rows are emitted in commit
order, one at a time, and lanes are assigned greedily on first sight. Crossings are simply
drawn as `/` over `|` and are visually ambiguous in dense histories — the well-known
complaint that motivated `git-graph` (below).

**Truncation.** `graph_needs_truncation()` honours `--graph-max-lanes` (`revs->graph_max_lanes`);
lanes beyond the cap are elided rather than reflowed. There is also a long-standing
`TODO` in `graph.c`: *"Limit the number of columns, similar to the way gitk does."*

Verbatim sample, from this repository (`git log --graph --oneline --all`):

```
*   e27a2561 Merge #150: delete a Bench, reporting what it holds
|\
| * 79969c90 feat(bench): delete a Bench, reporting what it holds
|/
*   3febc03f Merge #146: the Bench is the only pin store
|\
| * b673ae24 feat(bench): the Bench is the only pin store; delete the tracking file's pin half
|/
| *   513d7475 WIP on feat/150-delete-bench: b6104e16 Merge #149: switch Bench
|/|\
| | * 316244e2 untracked files on feat/150-delete-bench: b6104e16 Merge #149
| * c730606e index on feat/150-delete-bench: b6104e16 Merge #149
|/
*   b6104e16 Merge #149: switch Bench, and fail loudly on one that does not exist
```

**Assessment.**

| Property | Verdict |
|---|---|
| Good at | One row per entity, arbitrary-length labels to the right, stable identity per lane, colour-per-lane, streaming |
| Fails at | Crossing minimisation (none), undirected edges, cycles (impossible in git), long-range edges (lane stays alive the whole span), >8ish concurrent lanes becomes unreadable |
| Minimum width | Graph gutter is `2 × active_lanes` characters; 4 lanes ≈ 8 cols + label |
| 80 columns | Degrades gracefully — the gutter grows, the label column shrinks; `--graph-max-lanes` hard-caps it |
| Streamable | **Yes, fully.** This is the key property: git emits row *n* knowing only rows ≤ *n* plus a 2-commit lookahead (`graph->lookahead[2]`). Pipe to a pager, no global layout pass |

### 1.2 `git-graph` (Rust) — same lanes, better lane *assignment*

<https://github.com/mlange-42/git-graph>, docs <https://docs.rs/git-graph/latest/git_graph/graph/index.html>

Key difference from git: it does not use "branch = label on a commit", it uses
**branch = a path in the ancestry tree**, and assigns columns according to a *configured
branching model* (`git-flow`, `simple`, `none`, or user TOML). So `main` gets column 0,
`develop` column 1, feature branches to the right — the column ordering is semantic, not
first-seen order. It classifies merged branches by parsing merge-commit summary lines
(hence its documented limitation: *"summaries of merge commits should not be modified"*).

Styles: `normal/thin`, `round`, `bold`, `double`, `ascii` — so it does have Unicode
box-drawing output (`│ ├ ╯` family), with `--style ascii` as the graceful fallback for
terminals without UTF-8. `-S/--sparse` prints a less compact graph where merge lines point
to target *lines* rather than merge commits. Limitations it admits: origin-only remotes,
**no octopus merges** (>2 parents unsupported).

Relevance to twig: the *idea* of a domain-supplied lane ordering (here: branching model;
for twig: iteration, area path, or state) is directly transferable and is the single
biggest readability win over greedy first-seen lanes.

Interactive sibling: **git-igitt** <https://github.com/mlange-42/git-igitt> — the same
renderer inside a full TUI, proof that lane rendering survives being embedded in a
scrollable pane.

### 1.3 tig

<https://jonas.github.io/tig/> — the main view draws the same lane model as `git log --graph`
with its own implementation, adds per-lane colour and supports both ASCII and Unicode
graph glyph sets (`main-view-commit-title-graph = v2` etc. in tigrc). Nothing
algorithmically new versus git; it is the existence proof that the lane model works inside
a keyboard-driven, scroll-anchored TUI with a fixed-width gutter and selectable rows.

**Cross-cutting caveat for all of §1.** The lane model relies on a *global topological
order* of nodes rendered top to bottom with all edges pointing upward. Work-item
`Related` edges are undirected and can point anywhere, and Predecessor/Successor chains can
contain cycles in practice (ADO does not forbid them). A lane renderer for twig needs an
explicit answer for "edge points *down* the list" — git never faces this.

---

## 2. Layered (Sugiyama) layout on a character grid

### 2.1 `Graph::Easy` / `graph-easy` (Perl)

<https://metacpan.org/pod/Graph::Easy>, manual <http://bloodgate.com/perl/graph/manual/>

The most mature general-graph ASCII renderer that exists. Layouter documented at
<http://bloodgate.com/perl/graph/manual/layouter.html>. Three phases, verbatim from the manual:

1. *"categorizes the nodes and sorts them into groups according to their group information,
   as well as their rank"*
2. *"creates chains of nodes, starting with nodes with as little incoming edges as possible…
   The idea is to find the longest consecutive chains of nodes as possible"*
3. *"places the individual nodes on a checkered plane, much like a chess board. Once a node
   and its successors are placed, it tries to find the paths between the nodes to generate
   the edge cells."*

So: **not** classical Sugiyama. It is rank + longest-chain detangling + greedy placement on
an infinite integer grid, followed by **orthogonal edge routing where a heuristic is tried
first and A\* pathfinding is the fallback** (manual: *"For all other cases, and when the
heuristic couldn't find a path, a general algorithm called A\* is used"*). Edges may only
run horizontally or vertically — no diagonals — which is exactly the constraint a character
grid imposes. Each node occupies ≥1 cell; each edge occupies a run of cells.

The chain-finding matters. The manual shows the same graph before and after v0.25's
chain detection — verbatim:

```
  +------------------------------+
  v                              |
+---------+     +--------+     +--------+     +---------+
| Koblenz |  | Ulm    | --> | Bautzen |
+---------+     +--------+     +--------+     +---------+
  |               |                             |
  |               |                             |
  |               v                             |
  |             +--------+     +--------+       |
  +-----------> | Berlin | --> | Kassel |       |
                +--------+     +--------+       |
                  ^                             |
                  +-----------------------------+
```

versus, after chain detection:

```
  +--------------------------------------------+
  |                                            v
+------+     +---------+     +---------+     +--------+     +--------+
| Bonn | --> | Ulm     | --> | Bautzen | --> | Berlin | --> | Kassel |
+------+     +---------+     +---------+     +--------+     +--------+
  |            |                               ^
  |            |                               |
  |            v                               |
  |          +---------+                       |
  +--------> | Koblenz | ----------------------+
             +---------+
```

Edge *styles* survive into ASCII (dotted `:`, double `=`/`H`, plus `#` for bold/broad/wide,
which the docs admit all collapse to the same glyph). From the output manual page
<http://bloodgate.com/perl/graph/manual/output.html> — note the dotted node border and the
`#`/`H` double edge, which is the closest prior art to "different edge kinds get different
glyphs":

```
  #============================================#
  H                                            v
+---------+     ........     +---------+     +--------+     +--------+
| Bautzen | --> : Bonn : --> | Koblenz | --> | Berlin | --> | Kassel |
+---------+     :......:     +---------+     +--------+     +--------+
  ^               |            ^               ^
  :               +------------+---------------+
  :                            |
  :             +------+       |
  ............. | Ulm  | ------+
                +------+
```

Documented ASCII limitations: only two colours, arrows always rendered open regardless of
arrow style, no slanted/round node borders.

| Property | Verdict |
|---|---|
| Good at | True general graphs — cycles, back edges, undirected edges, multiple edge styles, labels on edges; genuinely readable at ~10 nodes |
| Fails at | Width explodes with node count (boxes are laid side by side); no reflow to a target width; Perl-only; slow |
| Minimum width | ≈ `(box_width + 5) × widest_rank`. 5 nodes across ≈ 60–70 cols; 8+ across blows past 200 |
| 80 columns | **Poorly.** No width constraint input; you get what you get and it wraps/garbles |
| Streamable | **No.** Global placement + global routing must finish before the first line is emitted |

### 2.2 `Diagon` — `GraphDAG` and `GraphPlanar`

<https://github.com/ArthurSonzogni/Diagon>, live at <https://diagon.arthursonzogni.com/>

C++, two graph generators. `GraphDAG` does layer assignment by longest-path rank and packs
node boxes **edge-to-edge with no gutter**, routing edges through the single-character
gaps using `┌ ┬ ▽ △ └ ┴` glyphs. Verbatim from the README:

```
┌─────┐┌─────────┐┌─────┐
│socks││underwear││shirt│
└┬────┘└┬─┬──────┘└┬─┬──┘
 │      │┌▽─────┐  │┌▽───────┐
 │      ││pants │  ││tie     │
 │      │└┬──┬──┘  │└┬───────┘
┌▽──────▽─▽┐┌▽─────▽┐│
│shoes     ││belt   ││
└──────────┘└┬──────┘│
┌────────────▽───────▽┐
│jacket               │
└─────────────────────┘
```

`GraphPlanar` handles **cycles** — note the `△` back-edge arrow feeding `loop → if`:

```
┌──────────┐
│    if    │
└△─┬──────┬┘
 │ │     ┌▽─────┐
 │ │     │then A│
 │ │     └┬─────┘
 │┌▽─────┐│
 ││then B││
 │└┬─────┘│
 │┌▽──────▽─┐
 ││   end   │
 │└┬────────┘
┌┴─▽─┐
│loop│
└────┘
```

This is the densest-per-character general-graph rendering in the survey: the Chromium
dependency example in the README fits ~10 nodes and ~18 edges in well under 80 columns.
The cost is that edge *identity* is hard to trace by eye — several parallel `│` runs
adjacent to each other with no colour.

| Property | Verdict |
|---|---|
| Good at | Extreme character density; cycles; deterministic, boring, legible glyph set; nodes stack vertically so height, not width, grows |
| Fails at | Edge disambiguation at scale; no edge labels; no edge kinds; no interactivity hooks (it emits a text blob) |
| Minimum width | Roughly `sum(label widths of the widest layer) + 2 per node`; the Chromium sample is ~60 cols for 10 nodes |
| 80 columns | **Yes, well** — the widest-layer bound is the binding constraint and it is usually small |
| Streamable | No — full layout before output |

### 2.3 `ascii-graphs` (Scala, mdr)

<https://github.com/mdr/ascii-graphs>. The README states it plainly: *"Layout is
Sugiyama-style layered graph drawing, and supports multi-edges, cycles, and vertex labels,
but not self-loops or edge labels."* Verbatim output:

```
             +---+
             |V7 |
             +---+
               |
               v
           +-------+
           |  V1   |
           +-------+
             |  ||
         -----  |--------
         |      ---     |
         v        |     |
      +-----+     |     |
      | V2  |     |     |
      +-----+     |     |
        | |       |     |
      --- ---     |     |
      |     |     |     |
      v     v     v     v
    +---+ +---+ +---+ +---+
    |V5 | |V6 | |V4 | |V3 |
    +---+ +---+ +---+ +---+
```

This is the clearest small example of the *bundled fan-out* idiom: an edge leaving a node
drops one row, then runs horizontally in a dedicated routing band to its target column.
Note the ambiguity even at 7 nodes — `|  ||` under V1 requires counting characters to know
which line is which. Last released 0.0.3; effectively unmaintained. Cited here for the
shape, not as a dependency.

### 2.4 `mermaid-ascii` (Go)

<https://github.com/AlexanderGrooff/mermaid-ascii>. Renders Mermaid `graph LR` / `graph TD`
to Unicode boxes with `►` `▼` arrowheads, tunable spacing (`-x`, `-p`) and **edge labels
inlined into the connector**. Verbatim:

```
┌───┐     ┌───┐         ┌───┐
│   │     │   │         │   │
│ A ├────►│ B ├─example►│ D │
│   │     │   │         │   │
└─┬─┘     └─┬─┘         └─┬─┘
  │         │             │
  │         │             │
  │         ▼             │
  │       ┌───┐           │
  └──────►│ C │◄──────────┘
          │   │
          └───┘
```

The `├─example►` inline edge label is the single most directly reusable idea for twig's
edge kinds (`├─pred►`, `├─rel──`). Note also `◄` on the incoming side of C: arrowheads are
drawn at the *target* box border, so bidirectional/undirected edges are expressible.

Cost: vertical padding is generous (4+ rows per box), so a 20-node graph is very tall.

### 2.5 `phart` (Python)

<https://github.com/scottvr/phart>. Worth flagging because it is the only tool in this
survey that treats **terminal width as a first-class layout constraint**. It offers
`--layout layered --constrained --target-canvas-width 80 --target-canvas-height 24`, which
partitions the graph during layout and emits *panelized* output with connector cues
between panels, plus `--paginate-output-width auto` (ANSI-aware slicing). It also supports
orthogonal/"Manhattan" edge routing, cycles, bidirectional edges, edge labels, and
attribute-driven edge colouring. Actively developed (2.0.x). If constrained-canvas
partitioning is the direction twig wants, this is the prior art to read.

### 2.6 The Graphviz baseline

Graphviz has no character-cell renderer. `-Tplain` (<https://graphviz.org/docs/outputs/plain/>)
emits float coordinates that a caller must quantise itself:

```
$ echo 'digraph { a->b }' | dot -Tplain
graph 1 0.75 1.5
node a 0.375 1.25 0.75 0.5 a solid ellipse black lightgrey
node b 0.375 0.25 0.75 0.5 b solid ellipse black lightgrey
edge a b 4 0.375 0.99 0.375 0.93 0.375 0.86 0.375 0.79 solid black
stop
```

A legitimate architecture is "let dot do layer assignment + crossing minimisation, then
snap to cells yourself". The catch: dot's output is continuous, its crossing minimisation
optimises for pixel space, and snapping to cells frequently reintroduces overlaps. No
widely-used tool does this successfully; treat it as a research path, not a shortcut.

---

## 3. Dependency/DAG viewers in TUIs — the "tree + dedup marker" school

This is the pragmatic mainstream, and it is what nearly every shipping developer tool
actually does: **render a spanning tree of the graph, and mark repeat visits.**

### 3.1 `cargo tree`

<https://doc.rust-lang.org/cargo/commands/cargo-tree.html>. Verbatim:

```
myproject v0.1.0 (/myproject)
└── rand v0.7.3
    ├── getrandom v0.1.14
    │   ├── cfg-if v0.1.10
    │   └── libc v0.2.68
    ├── libc v0.2.68 (*)
    ├── rand_chacha v0.2.2
    │   ├── ppv-lite86 v0.2.6
    │   └── rand_core v0.5.1
    │       └── getrandom v0.1.14 (*)
    └── rand_core v0.5.1 (*)
[build-dependencies]
└── cc v1.0.50
```

Docs: *"Packages marked with `(*)` have been 'de-duplicated'. The dependencies for the
package have already been shown elsewhere in the graph, and so are not repeated. Use the
`--no-dedupe` option to repeat the duplicates."*

Two more things cargo does that matter for twig:

- **`[build-dependencies]` / `[dev-dependencies]` section headers**: different *edge kinds*
  become separate trees under a header rather than differently-styled edges in one tree.
  This is the cheapest correct answer for Related vs Predecessor vs Successor.
- **`-i/--invert <spec>`**: reverse the edge direction and re-root the tree on a chosen
  node. Answering "what depends on X" by re-rooting instead of drawing a bidirectional
  graph. `--prune` and `--depth` bound the output.

### 3.2 `npm ls`

<https://docs.npmjs.com/cli/v11/commands/npm-ls/>. Same idea, different marker — the word
`deduped`. Verbatim from a real run in `/tmp` on `express@4`:

```
  ├─┬ body-parser@1.20.6
  │ ├── bytes@3.1.2
  │ ├── content-type@1.0.5 deduped
  │ ├── debug@2.6.9 deduped
  │ ├── depd@2.0.0 deduped
  │ ├── destroy@1.2.0
  │ ├── http-errors@2.0.1 deduped
  │ ├─┬ iconv-lite@0.4.24
  │ │ └── safer-buffer@2.1.2
  │ ├── on-finished@2.4.1 deduped
```

Note `─┬` vs `──` as the leaf/branch indicator — a one-character "this node has hidden
children" affordance, distinct from the dedup marker. npm's `deduped` semantics have been
a documented source of user confusion (<https://github.com/npm/npm/issues/19861>), which is
a warning: **a dedup marker that does not say *where* the full expansion lives is a
usability bug.** cargo's `(*)` has the same defect. For twig, a back-reference marker
should carry a locator (e.g. `(*) → #1234 above`, or a line number).

### 3.3 `nix why-depends`

<https://nix.dev/manual/nix/2.26/command-ref/new-cli/nix3-why-depends>. Not a full graph —
it prints the *shortest dependency path* between two nodes as an indented chain, with
`→` marking the resolved edge target and the surrounding context showing *why* (the byte
offset in a file where the reference appears). Verbatim shape:

```
├───lib/thunderbird/libxul.so: …libXt-1.2.0/lib:/nix/store/1qj29…-libXdamage-1.1.5/lib:/nix/…
│   → /nix/store/1qj29ipxl2fyi2b13l39hdircq17gnk0-libXdamage-1.1.5
│   ├───lib/libXdamage.so.1.1.0: …-libXfixes-5.0.3/lib:/nix/store/adzfj…-libX11-1.7.0/lib:…
│   │   → /nix/store/adzfjjh8w25vdr0xdx9x16ah4f5rqrw5-libX11-1.7.0
```

The pattern worth stealing: **do not draw the graph — answer a path query about it.**
"Why is #1234 blocked?" → print the predecessor chain. This sidesteps layout entirely and
is trivially streamable.

### 3.4 Bazel

<https://bazel.build/query/quickstart>. `bazel query 'deps(:runner)' --output graph` emits
**Graphviz DOT source**, not text art — Bazel deliberately delegates rendering. Its
in-terminal formats are `--output label` (a flat list) and `--output label_kind`.
`--graph:factored` merges equivalent nodes to shrink the graph. Takeaway: a very large,
very mature dependency tool concluded that terminal node-link rendering was not worth
building, and shipped a flat list plus a DOT export instead. That is a data point about
expected value, not a counsel of despair — but it should be weighed.

### 3.5 `k9s`, `lazygit`, `dua`, `btop`

Checked for graph rendering; all four are **tree or list renderers**, not graph renderers:

- `k9s` (<https://k9scli.io/>) — tabular resource views; relationships are navigated
  (`Enter` to drill from Deployment → ReplicaSet → Pod), never drawn. Ownership is a
  graph and k9s renders it as *navigation*, not as a picture.
- `lazygit` (<https://github.com/jesseduffield/lazygit>) — commit panel draws git lane
  graphs (same model as §1) in a narrow pane; everything else is lists.
- `dua` / `ncdu` — nested size trees, strict hierarchy.
- `btop` (<https://github.com/aristocratos/btop>) — process "tree" mode is strict
  parent-PID hierarchy with `├─`/`└─`.

The absence is the finding: **no mainstream TUI draws a general node-link graph.** They
either restrict to trees, or make the graph navigable one hop at a time.

| Property (tree + dedup marker) | Verdict |
|---|---|
| Good at | Arbitrary node count, arbitrary label length, trivially scrollable, everyone already reads it, one row per node so selection/keybindings are obvious |
| Fails at | Hides the graph structure — you cannot see that two branches converge, only that a marker says so; undirected `Related` has no natural root; cycles need explicit cutting |
| Minimum width | `2 × depth + label`. Depth is bounded by cycle-cutting, so ~40 cols is enough |
| 80 columns | **Yes, best in class** |
| Streamable | **Yes**, with one caveat: the dedup marker needs a "seen" set, so you must emit in DFS order and can only mark *backward* references |

---

## 4. Non-node-link alternatives

### 4.1 Edge lists grouped by relation kind

The dullest option and the one with the most shipping precedent — cargo's
`[build-dependencies]` header, `go mod graph` (one `from to` pair per line), `nix-store -q --references`.
Shape:

```
#1234  Bench delete command
  Predecessor  ← #1201  Bench create
  Successor    → #1290  Bench rename
  Related      ~ #1150, #1188
```

- **Good at**: exact, unambiguous, greppable, machine-parseable, streamable, zero layout
  code, edge kinds are free (they're headers).
- **Fails at**: conveys no shape — you cannot see chains, convergence, or cycles.
- **Width**: ~40 cols. Degrades perfectly.

### 4.2 Indented outline with back-reference markers

§3's tree plus an explicit locator on the repeat, e.g. `#1150 (↑ see line 4)` or a
stable per-node ordinal `[7]` printed on first appearance and referenced thereafter.
This is the fix for the npm `deduped` confusion. It is a *convention*, not a tool — no
surveyed CLI does the locator properly, so twig would be slightly ahead of the field here.
Cheap and safe.

### 4.3 Adjacency matrices

Rare in terminals; no mainstream CLI ships one. The theoretical shape:

```
        1234 1201 1290 1150 1188
  1234    .    P    S    R    R
  1201    S    .    .    .    .
  1290    P    .    .    .    .
  1150    R    .    .    .    .
  1188    R    .    .    .    .
```

- **Good at**: every edge visible with zero routing; O(1) lookup of "is A related to B";
  density is instantly readable; edge kinds are single letters.
- **Fails at**: **O(n²) width.** At 4 cols/node, 15 nodes = 60 + label gutter ≈ 80 cols;
  25 nodes = 100+; 50 nodes is 200+ and unusable. Node labels must be reduced to short IDs,
  which for work items is actually fine (`1234`). Reading a path (A→B→C) requires two
  lookups; humans are bad at this.
- **80 columns**: only up to ~15 nodes.
- **Streamable**: no (needs the full node set to size columns), but computable in one pass.

Verdict: viable *only* as a secondary "density/overview" view for a filtered subset, not
as the primary rendering. Its rarity in real tools is a genuine warning sign.

### 4.4 Arc diagrams

Nodes on one axis, edges as arcs above/below. Standard in HTML/SVG (D3 has an idiom);
**effectively nonexistent in terminals** — the survey found no shipping CLI that renders
one, because the arc height needed to avoid collisions is many rows and character cells
render arcs badly. The closest real-world analogue is the git lane gutter (§1), which is
an arc diagram with all arcs collapsed onto vertical lanes. Report this as unproven; do
not prototype it first.

---

## 5. Ranked shortlist for twig (5–50 nodes; Related / Predecessor / Successor)

Ranked by expected value = (readability × implementation cost × risk).

**1. Spanning tree + typed edge markers + located back-references.** (§3.1, §3.2, §4.2)
Root on the selected work item, DFS over all three edge kinds, prefix each child row with
its edge kind glyph, and emit `(*)` with an explicit locator on repeat visits. Degrades to
80 cols, streams, reuses twig's existing "row with children" node type almost unchanged,
and is the shape every developer already reads. It is the only option on this list that is
low-risk *and* low-cost. Prototype it first even if you intend to ship something else,
because it is the baseline everything else must beat.

**2. Git-style lane gutter with a semantic lane ordering.** (§1.1, §1.2)
One row per work item, fixed-width gutter on the left carrying `│ ├ ╭ ╯` lanes for
Predecessor/Successor chains, colour per lane, label to the right. Adopt git-graph's
lesson: assign lanes from a domain ordering (iteration or state), not first-seen. Streams,
scrolls, keeps one-row-per-item selection semantics, fits the existing TUI. Two hard
problems git never had to solve: edges that point *down* the list, and undirected
`Related`. Suggested resolution — render only Predecessor/Successor in the gutter (they
are a DAG in practice), and show `Related` as a §4.1 sidebar or an inline count.

**3. Diagon-style dense layered boxes for a filtered subgraph.** (§2.2, §2.4)
Only worth it for "show me the neighbourhood of #1234", 5–15 nodes, non-scrolling. Gets
you a real picture, fits 80 cols, handles cycles. Costs a real layout engine (layer
assignment + crossing minimisation + orthogonal routing), does not stream, and does not
give you per-row selection. Borrow mermaid-ascii's inline edge label (`├─pred►`) for the
edge kinds. Prototype third, as a "detail/expand" mode, not the main view.

**4. Edge list grouped by relation kind.** (§4.1)
Not a graph rendering, but it is the correct fallback for narrow terminals, `--no-graph`,
piping, and machine output, and it should exist regardless of what wins above. Nearly
free.

**5. Adjacency matrix.** (§4.3)
Prototype only as an experiment for ≤15-node filtered sets. O(n²) width kills it at twig's
stated upper bound, and there is essentially no prior art to copy.

**Not recommended:** arc diagrams (§4.4 — no terminal prior art), and Graphviz `-Tplain`
coordinate snapping (§2.6 — no successful precedent).

### One thing worth testing early

Every approach above except §4.1 and §4.3 needs a **cycle-cutting rule** (work-item
Predecessor/Successor graphs do contain cycles in the wild, and `Related` is undirected so
every edge is trivially a 2-cycle). Decide and test that rule — DFS back-edge detection,
marked and rendered as a back-reference — before choosing a renderer, because it constrains
all of them equally.

---

## Source index

- git `graph.c` — <https://github.com/git/git/blob/master/graph.c>
- `git log` docs — <https://git-scm.com/docs/git-log>
- git-graph — <https://github.com/mlange-42/git-graph>, <https://docs.rs/git-graph/latest/git_graph/graph/index.html>
- git-igitt — <https://github.com/mlange-42/git-igitt>
- tig — <https://jonas.github.io/tig/>
- Graph::Easy — <https://metacpan.org/pod/Graph::Easy>, layouter <http://bloodgate.com/perl/graph/manual/layouter.html>, output formats <http://bloodgate.com/perl/graph/manual/output.html>
- Diagon — <https://github.com/ArthurSonzogni/Diagon>, <https://diagon.arthursonzogni.com/>
- ascii-graphs — <https://github.com/mdr/ascii-graphs>
- mermaid-ascii — <https://github.com/AlexanderGrooff/mermaid-ascii>
- phart — <https://github.com/scottvr/phart>
- Graphviz plain output — <https://graphviz.org/docs/outputs/plain/>
- cargo tree — <https://doc.rust-lang.org/cargo/commands/cargo-tree.html>
- npm ls — <https://docs.npmjs.com/cli/v11/commands/npm-ls/>, dedup confusion <https://github.com/npm/npm/issues/19861>
- nix why-depends — <https://nix.dev/manual/nix/2.26/command-ref/new-cli/nix3-why-depends>
- Bazel query — <https://bazel.build/query/quickstart>, <https://bazel.build/query/language>
- k9s — <https://k9scli.io/> · lazygit — <https://github.com/jesseduffield/lazygit> · btop — <https://github.com/aristocratos/btop>
