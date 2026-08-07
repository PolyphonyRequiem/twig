# Console graph glyphs and interaction: a survey

Scope: character-level and interaction mechanics for drawing graph/tree structures in a terminal.
Written for twig (.NET, Spectre.Console for CLI output, Terminal.Gui for the TUI), to inform how
Azure DevOps work-item relationship graphs should look.

Bias of this document: prior art over invention. Where something is rare, unproven, or where I
could only find one implementation, it is labelled as such. Claims about glyph inventory and
property values were verified locally against the Unicode Character Database and against the
actual source of the tools cited, not from memory.

---

## 1. Box-drawing and line glyph vocabularies

### 1.1 The block

Unicode Box Drawing, U+2500–U+257F, 128 code points, all assigned.
Chart: https://www.unicode.org/charts/PDF/U2500.pdf
Block reference: https://en.wikipedia.org/wiki/Box-drawing_character

The block is organised as a small combinatorial system over four arms (up/down/left/right), where
each arm can be **absent**, **light**, or **heavy** — plus a separate **double** family and a
separate **arc (rounded)** family.

Verified inventory (enumerated locally from the UCD):

**Straight lines**
- Light: `─` U+2500, `│` U+2502
- Heavy: `━` U+2501, `┃` U+2503
- Double: `═` U+2550, `║` U+2551
- Dashed, light and heavy, in double/triple/quadruple dash: U+254C–U+254F (double dash),
  U+2504–U+250B (triple and quadruple dash). Note the dash families exist for light and heavy
  **only** — there is no double-struck dashed line.

**Corners**
- Light square: `┌` U+250C, `┐` U+2510, `└` U+2514, `┘` U+2518
- Heavy square: `┏` U+250F, `┓` U+2513, `┗` U+2517, `┛` U+251B
- Mixed light/heavy corners: all eight exist, e.g. `┍` U+250D (down light, right heavy),
  `┎` U+250E (down heavy, right light). Every corner has all four light/heavy combinations.
- Double square: `╔` U+2554, `╗` U+2557, `╚` U+255A, `╝` U+255D
- Mixed single/double corners: exist, e.g. `╒` U+2552 (down single, right double), `╓` U+2553.
- **Arc (rounded): only four glyphs, only light.** `╭` U+256D, `╮` U+256E, `╯` U+256F,
  `╰` U+2570. There is **no heavy rounded corner, no double rounded corner, and no rounded
  T-junction or rounded cross.** This is the single most important asymmetry in the block: if you
  adopt rounded corners you are committed to light weight for everything a corner touches, or to
  a visible weight discontinuity.

**T-junctions (tees)** — U+251C–U+253B. Every light/heavy combination of the three arms exists
(e.g. `┞` U+251E "up heavy and right down light"). Double family tees: U+255E–U+2569, but only
the *uniform* single/double splits — e.g. `╞` (vertical single, right double), `╟` (vertical
double, right single). There is **no mixed heavy/double** junction anywhere.

**Crosses** — U+253C–U+254B. Sixteen glyphs covering every light/heavy assignment of the four
arms of a `+` crossing: `┼` all light, `╋` all heavy, `┿` (vertical light, horizontal heavy),
`╂` (vertical heavy, horizontal light), `╀`, `╁`, `╃`, `╄`, `╅`, `╆`, `╇`, `╈`, `╉`, `╊`, `┽`, `┾`.
Double crosses: `╪` U+256A (vertical single, horizontal double), `╫` U+256B, `╬` U+256C.

**Stubs / half-lines** — U+2574–U+257B (`╴ ╵ ╶ ╷` light, `╸ ╹ ╺ ╻` heavy) and the transition
glyphs U+257C–U+257F (`╼ ╽ ╾ ╿`) which change weight mid-cell. These four are the only
weight-transition glyphs; there is no single↔double transition glyph.

**Diagonals** — only three, and only light: `╱` U+2571, `╲` U+2572, `╳` U+2573.

### 1.2 Which combinations are impossible

These are hard gaps, confirmed by enumerating the block:

1. **Heavy × double crossings and junctions.** The block's combinatorics cover light×heavy fully
   and single×double partially. Nothing mixes heavy with double. If you use `━` heavy for one
   edge kind and `═` double for another, you cannot draw the cell where they cross.
2. **Rounded anything except the four light corners.** No `╭`-weight tee, cross, heavy arc, or
   double arc.
3. **Dashed junctions.** Dashes exist as line segments only. There is no dashed corner, dashed
   tee, or dashed cross. A dashed edge that turns or crosses must borrow a solid junction glyph.
   This is what real tools do, and it reads acceptably because the junction is one cell.
4. **Diagonal junctions.** There is no glyph for "diagonal crossing a vertical", "diagonal
   meeting a horizontal", or a diagonal corner. `╳` is two diagonals crossing each other and
   nothing else. Diagonals in this block are essentially non-composable.
5. **Crossings that stay distinguishable.** There is no "line hops over line" (bridge/jump)
   glyph anywhere in the block. Print media uses a small arc hop; the terminal has no such cell.

### 1.3 The actual inventory for "an edge that must cross another edge"

This is the crux question for a relationship graph, so, concretely, your options are:

| Technique | Glyphs | Cost |
|---|---|---|
| Plain cross | `┼` (or the 15 weight variants) | Ambiguous: reader cannot tell crossing from junction. Only disambiguated if the two edges differ in *weight*, e.g. `┿` = light vertical crossed by heavy horizontal. That gives you **exactly 2 distinguishable edge classes** at a crossing, not more. |
| Weight-differentiated cross | `┼ ┿ ╂ ╋` plus asymmetric `╀ ╁ ╃ ╄ ╅ ╆ ╇ ╈ ╉ ╊ ┽ ┾` | Works, but the asymmetric ones are for *junctions of mixed weight*, not for clean crossings; only `┼ ┿ ╂ ╋` are true "both lines pass through". |
| Single/double cross | `╪ ╫ ╬` | Same 2-class ceiling, and double lines are Ambiguous-width (see §2). |
| Break the crossed line | draw one line, leave a gap or a stub `╴ ╶` in the other | Unambiguous, ugly, costs a cell of information. |
| Colour the crossing | `┼` styled per-edge — impossible, the cell has one foreground colour | **Not available.** A crossing cell can only carry one colour. This is a real limitation for colour-encoded edge kinds. |
| Avoid crossings entirely | route as an indented tree with reference markers (§5) | What almost every shipping terminal tool actually does. |
| Legacy Computing diagonals | U+1FBA0–U+1FBAF, e.g. `🮠 🮡 🮢 🮣` and the "horizontal with vertical stroke" `🮯` U+1FBAF | **Do not use.** Supplementary plane (surrogate pairs), and font coverage is close to nil — Wikipedia notes "few fonts support these characters", Noto Sans Symbols 2 only partially. See §2 and §6 for the width hazard. |

**Bottom line for twig:** the terminal's crossing vocabulary supports at most two visually
distinct edge weights at a crossing point, and no colour differentiation at all in the crossing
cell. Any design that needs 5+ ADO relation kinds (Parent, Child, Related, Duplicate, Successor,
Predecessor, Affects, Tested By…) simultaneously visible at crossing points **cannot be drawn
with line geometry alone.** Plan around it rather than fighting it.

---

## 2. Terminal reality constraints

### 2.1 Cell aspect ratio

Monospace terminal cells are typically about 1:2 wide:tall (a cell advance width of roughly half
the line height). https://en.wikipedia.org/wiki/Monospaced_font

Consequences for graph geometry, and they are severe:

- **A "square" box is not square.** A 10×5 box of box-drawing characters renders as roughly a
  square on screen. Any layout algorithm that reasons in cells must apply an ~2:1 x-scale before
  it means anything visually.
- **Diagonals are wrong by construction.** `╱` draws a line at 45° *within its cell*, but because
  cells are 1:2, a run of `╱` down consecutive rows traces a visual slope of about 63°, not 45°.
  Diagonal edges therefore look steeper than the layout intends. `git log --graph` lives with
  this; it is one reason its diagonal runs read as near-vertical.
- **Vertical density is scarce, horizontal density is cheap.** One extra row costs twice the
  visual space of one extra column. This argues for **wide-and-shallow** layouts (indented trees
  reading left-to-right, columns to the right of a node label) rather than tall stacked ones —
  the opposite of the intuition from graphviz-style drawings.
- **Force-directed / spring layouts do not transfer.** They assume isotropic space. In a terminal
  you would need to distort the distance metric 2:1 to get comparable results, and even then you
  land on non-integer positions that must be quantised to cells.

### 2.2 East Asian Width — the property that decides how wide your line is

Normative report: **UAX #11, East Asian Width** — https://www.unicode.org/reports/tr11/
Data file: https://www.unicode.org/Public/UCD/latest/ucd/EastAsianWidth.txt

Six default values: Ambiguous, Fullwidth, Halfwidth, Narrow, Wide, Neutral. For any operation
these resolve to two: narrow or wide, **depending on context** (UAX #11 §3).

UAX #11 contains an explicit warning that is directly relevant here:

> "The East_Asian_Width property is not intended for use by modern terminal emulators without
> appropriate tailoring on a case-by-case basis. Such terminal emulators need a way to resolve
> the halfwidth/fullwidth dichotomy that is necessary for such environments, but the
> East_Asian_Width property does not provide an off-the-shelf solution for all situations."
> — UAX #11 §2, https://www.unicode.org/reports/tr11/

**And here is the hazard for box-drawing specifically.** Verified against the live UCD file:

```
2500..254B     ; A   # BOX DRAWINGS LIGHT HORIZONTAL..HEAVY VERTICAL AND HORIZONTAL
254C..254F     ; N   # LIGHT DOUBLE DASH HORIZONTAL..HEAVY DOUBLE DASH VERTICAL
2550..2573     ; A   # DOUBLE HORIZONTAL..LIGHT DIAGONAL CROSS
2574..257F     ; N   # LIGHT LEFT..HEAVY UP AND LIGHT DOWN
E000..F8FF     ; A   # BMP Private Use Area
F0000..FFFFD   ; A   # Supplementary Private Use Area-A
1FB00..1FBEF   ; N   # Symbols for Legacy Computing
```

So **most of the box-drawing block, including every corner, tee, cross, and the rounded arcs, is
East_Asian_Width = Ambiguous (A)**, not Narrow. Ambiguous means: 1 cell in a Western context,
**2 cells in an East Asian context**. A terminal or a font configured for CJK (or a user with
`ambiguous-width=double` set — a common setting in East Asian locales, and a known source of tmux
misalignment reports, e.g. https://github.com/wez/wezterm/issues/3704) will render your tree
guides double-width and every column after them will be off.

This is not theoretical: it is the same class of bug as the SPUA one already documented in
twig's `IconSet.cs`. The mitigation is the same: **you cannot fix it with padding, you can only
avoid depending on it.** Pure-ASCII fallback (`|`, `-`, `+`, `` ` ``) is EAW=Na and is the only
tier that is width-safe under every terminal configuration.

Also note the odd asymmetry: the dashed lines (U+254C–254F) and the stubs/transitions
(U+2574–257F) are **Neutral**, while the solid lines next to them are **Ambiguous**. Mixing them
in one guide string can produce inconsistent width behaviour on the same row.

### 2.3 Combining marks and zero width

UAX #11 §1: "The East_Asian_Width property does not preserve canonical equivalence, because the
base characters of canonical decompositions almost always have a different East_Asian_Width
property value than the precomposed characters. Decomposing a character, and applying the
East_Asian_Width property to a base character and combining marks separately does not yield the
expected values."

Practically: combining marks (general category Mn/Me) are width 0. If a work-item title contains
combining diacritics, naive `string.Length` overcounts and the row runs long; if the renderer
uses grapheme-aware measurement it is correct. Emoji sequences are worse — ZWJ (U+200D) sequences
and variation selector VS16 (U+FE0F) change width in ways that depend on the Unicode version the
measuring library was built against.

### 2.4 Why glyphs measure as zero width in .NET — the concrete mechanism

Spectre.Console measures cells via `Spectre.Console/Internal/Cell.cs`, which delegates to the
`Wcwidth` package (`UnicodeCalculator.GetWidth`).
- https://github.com/spectreconsole/spectre.console/blob/main/src/Spectre.Console/Internal/Cell.cs
- https://github.com/spectreconsole/wcwidth

Read the source. `Cell.GetCellLength(char rune)` caches into `sbyte[char.MaxValue + 1]` — a table
indexed by **UTF-16 code unit**, not by scalar. On the `NETSTANDARD2_0` path, `GetCellLength(string)`
literally does `foreach (var rune in text)` — iterating `char`, i.e. code units. A supplementary
character arrives as a high surrogate (U+D800–U+DBFF) and a low surrogate (U+DC00–U+DFFF), each
measured independently; surrogates are general category Cs and are not in the wide table, so each
half contributes 0 or a wrong value. That is exactly the failure mode documented in twig's
`IconSet.cs` for nf-md-* Material Design icons at U+F0001–U+F1AF0.

On modern targets `GetWidth(string)` uses `EnumerateRunes()` and is scalar-correct, and it has
explicit ZWJ and VS16 handling — but width is still resolved from tables that treat PUA as
Ambiguous, and the caching layer in `Cell` is still code-unit indexed. Related upstream report:
https://github.com/spectreconsole/spectre.console/issues/2086 ("Emoji width isn't measured
correctly", closed) — and note that Microsoft's own Aspire CLI shipped a private `EmojiWidth`
workaround table rather than trusting the library.

**Generalisation: any codepoint above U+FFFF is suspect in this stack.** That rules out Symbols
for Legacy Computing (U+1FB00–U+1FBEF, including all the fine-grained diagonals) *and* SPUA
Nerd Font icons, on identical grounds. Everything recommended in §6 is BMP.

Terminal.Gui measures with `Rune.GetColumns()` (see `Branch.GetWidth()` in
https://github.com/gui-cs/Terminal.Gui/blob/develop/Terminal.Gui/Views/TreeView/Branch.cs) —
scalar-based, so it does not have the surrogate-halving bug, but it is still subject to the
Ambiguous-width question and to whatever the host terminal actually does.

---

## 3. Fallback tiers: Unicode → ASCII

Real implementations, with both outputs. These were run locally or read from source.

### 3.1 `tree(1)` — `--charset=ascii`

Man page documents `--charset[=]charset` ("Set the character set to use when outputting HTML and
for line drawing") and `-A` / `--charset=IBM437`. https://linux.die.net/man/1/tree

Default (UTF-8):
```
tdemo
└── a
    ├── b
    │   └── f.txt
    └── g.txt
```
`tree --charset=ascii`:
```
tdemo
`-- a
    |-- b
    |   `-- f.txt
    `-- g.txt
```
Note the ASCII tier uses `` ` `` for the last-child corner, not `\`. That choice is near-universal.

### 3.2 `cargo tree --charset {utf8|ascii}`

"Chooses the character set to use for the tree. Valid values are 'utf8' or 'ascii'. When
unspecified, cargo will auto-select a value."
https://doc.rust-lang.org/cargo/commands/cargo-tree.html

Auto-selection is the important part: the tool decides rather than making the user configure it.

### 3.3 Spectre.Console `TreeGuide` — directly relevant to twig

Four guides, each a 4-part table (`Space`, `Continue`, `Fork`, `End` —
https://github.com/spectreconsole/spectre.console/blob/main/src/Spectre.Console/Rendering/TreeGuidePart.cs):

| Guide | Space | Continue | Fork | End |
|---|---|---|---|---|
| `AsciiTreeGuide` | `␣␣␣␣` | `\|␣␣␣` | `\|--␣` | `` `--␣ `` |
| `LineTreeGuide` | `␣␣␣␣` | `│␣␣␣` | `├──␣` | `└──␣` |
| `BoldLineTreeGuide` | `␣␣␣␣` | `┃␣␣␣` | `┣━━␣` | `┗━━␣` |
| `DoubleLineTreeGuide` | `␣␣␣␣` | `║␣␣␣` | `╠══␣` | `╚══␣` |

Sources: https://github.com/spectreconsole/spectre.console/tree/main/src/Spectre.Console/Rendering/Tree

The fallback mechanism is explicit and worth copying: `BoldLineTreeGuide` and
`DoubleLineTreeGuide` both override `SafeTreeGuide => Ascii`, and `Tree.cs` calls
`Guide.GetSafeTreeGuide(safe: !options.Unicode)`. So the heavy and double families degrade
**straight to ASCII**, skipping the light family. `LineTreeGuide` does not declare a safe
fallback at all — light box-drawing is treated as the baseline-safe Unicode tier.

Also note `Tree.cs` maintains `var visitedNodes = new HashSet<TreeNode>()` and throws
`CircularTreeException("Cycle detected in tree - unable to render.")`. **Spectre.Console's `Tree`
cannot render a cyclic graph at all.** For work-item relations — which are a general digraph,
not a tree — twig must flatten to a DAG/spanning tree itself before handing anything to `Tree`.

### 3.4 Rich (Python) — three Unicode tiers plus ASCII

```python
ASCII_GUIDES = ("    ", "|   ", "+-- ", "`-- ")
TREE_GUIDES = [
    ("    ", "│   ", "├── ", "└── "),
    ("    ", "┃   ", "┣━━ ", "┗━━ "),
    ("    ", "║   ", "╠══ ", "╚══ "),
]
```
https://github.com/Textualize/rich/blob/master/rich/tree.py

Two things here that Spectre.Console does not have. First, Rich uses the three Unicode families
as **depth encoding** — `guide_style` and nesting depth select light/heavy/double, so weight
carries hierarchy level rather than edge kind. Second, `ASCII_GUIDES` is selected automatically
from `Console.ascii_only`, i.e. from the detected encoding, not from a flag. Note the ASCII fork
is `+--`, where `tree(1)` uses `|--`.

### 3.5 Sapling / Jujutsu — four tiers, and one nobody else has

`jj` exposes `ui.graph.style` with values `"curved"` (default), `"square"`, `"ascii"`,
`"ascii-large"`. https://github.com/jj-vcs/jj/blob/main/docs/config.md

It delegates to Sapling's `renderdag`, whose glyph tables are, verbatim
(https://github.com/facebook/sapling/blob/main/eden/scm/lib/renderdag/src/pipeline/row_shape_to_prefix_lines/box_drawing.rs):

```rust
// square
["  ", "──", "│ ", "· ", "┘ ", "└─", "┴─", "┐ ", "┌─", "┬─", "┤ ", "├─", "┼─", "~ "];
// curved
["  ", "──", "│ ", "╷ ", "╯ ", "╰─", "┴─", "╮ ", "╭─", "┬─", "┤ ", "├─", "┼─", "~ "];
// DEC special graphics
["  ", "\x1B(0qq\x1B(B", "\x1B(0x \x1B(B", ... ];
```

Observe: the **curved** set uses `╭ ╮ ╯ ╰` for corners but falls back to the *square* `┴ ┬ ┤ ├ ┼`
for every junction — because, as established in §1.2, rounded tees and crosses do not exist. This
is the canonical demonstration of that gap in shipping code.

The fourth tier is **DEC Special Graphics**: `ESC ( 0` switches the terminal into the VT100
line-drawing character set, where ASCII `q` draws a horizontal line, `x` a vertical, `lkmjtuvwn`
the corners/tees/cross, and `ESC ( B` switches back. This is 7-bit-clean on the wire, needs no
Unicode support, no font with box-drawing coverage, and **has no width ambiguity whatsoever** —
every glyph is a plain ASCII byte as far as measurement is concerned. It is genuinely underused
prior art. Caveat: it requires the renderer to emit raw escape sequences and to survive them
passing through any layout engine that measures strings — which rules it out for Spectre.Console
markup and probably for Terminal.Gui, both of which own the escape stream.

### 3.6 Terminal.Gui

`TreeStyle.ShowBranchLines` (bool) toggles branch lines vs. plain whitespace; expand/collapse
symbols are `Glyphs.Expand` / `Glyphs.Collapse`, defaulting to `+` and `-` per the XML docs, and
are `Rune?` — set to null to hide. Leaf rows use `Glyphs.HLine` when branch lines are on, space
when off. Sources:
- https://github.com/gui-cs/Terminal.Gui/blob/develop/Terminal.Gui/Views/TreeView/TreeStyle.cs
- https://github.com/gui-cs/Terminal.Gui/blob/develop/Terminal.Gui/Views/TreeView/Branch.cs

Terminal.Gui has a global `Glyphs` table (`Terminal.Gui/Drawing/Glyphs.cs`) with settable
`HLine`, `VLine`, `HLineDbl`, `VLineDbl`, and the dashed variants — so an ASCII tier is achieved
by reassigning `Glyphs`, not by a per-widget flag. There is **no built-in `--ascii` mode**; twig
would have to build one.

### 3.7 `git log --graph` — how a real crossing-capable renderer degrades

git has no Unicode tier at all; it is pure ASCII, and it is the most widely-read graph rendering
in existence. Actual output from a locally constructed repo with two merges:

```
*   91e60a6 merge-feat2
|\
| * 45d1ab2 g1
* |   ec6ca8d merge-feat
|\ \
| |/
|/|
| * d9fbab7 f1
* | c5107b6 m3
|/
* 6f643a1 c2
* c9a2064 c1
```

Note the three-row sequence `|\ \` / `| |/` / `|/|`. git spends **whole extra rows** on
re-routing rather than trying to express a crossing in one cell. The vocabulary is `* | \ / _`
and nothing else. This is the strongest empirical evidence available that, in a terminal,
**crossings are paid for in rows, not in glyphs.** Documentation: https://git-scm.com/docs/git-log

---

## 4. Colour and emphasis as a substitute for geometry

### 4.1 The idea

Since the crossing vocabulary caps out at ~2 distinguishable edge weights (§1.3), the obvious
move is to encode relation *kind* in colour/intensity and keep geometry uniform.

### 4.2 What prior art actually exists

Honest answer: **thin.** I could not find a terminal graph tool that encodes *edge relation type*
by edge colour as its primary mechanism. What exists:

- **Rich/Spectre `guide_style`** — a single style applied to all guides, or varying by depth
  (Rich's three-family `TREE_GUIDES` indexed by nesting level). This encodes *depth*, not kind.
  https://rich.readthedocs.io/en/stable/tree.html
- **`tree(1)` colourisation via `LS_COLORS`/`TREE_COLORS`** — colours the *nodes* (by file type),
  never the guide lines. Also honours `NO_COLOR`.
- **`cargo tree -e <kinds>`** — distinguishes normal/build/dev dependency kinds not by colour but
  by **splitting into separately-labelled sections** (`[build-dependencies]`) and by filtering.
  https://doc.rust-lang.org/cargo/commands/cargo-tree.html
- **lazygit / gitui / jj** — colour commit graph lanes by *lane index* (to help the eye follow a
  branch across rows), not by relation semantics.

So the dominant prior art for "several kinds of relation" in terminal graphs is **partition and
filter**, not colour-encode. That is a meaningful finding: the tools that faced this problem
mostly declined to solve it visually.

Where colour *is* used for kind, it is on the **node label or a text badge**, not the connector.
That sidesteps the crossing-cell problem entirely (§1.3: a crossing cell has one foreground
colour and cannot represent two edges).

### 4.3 Accessibility limits

- **WCAG 2.1 SC 1.4.1 Use of Color (Level A):** "Color is not used as the only visual means of
  conveying information, indicating an action, prompting a response, or distinguishing a visual
  element." https://www.w3.org/WAI/WCAG21/Understanding/use-of-color.html
  This is dispositive: any edge-kind encoding must have a non-colour carrier (a text label, a
  glyph, a section heading) available.
- **WCAG 2.1 SC 1.4.11 Non-text Contrast (AA)** requires 3:1 contrast for graphical objects
  needed to understand content. https://www.w3.org/WAI/WCAG21/Understanding/non-text-contrast.html
  Terminal "dim" (SGR 2) frequently fails this against common backgrounds, and its rendering is
  entirely terminal-dependent — some terminals ignore SGR 2 outright, some render it as a
  different colour, some as reduced alpha.
- **`NO_COLOR`** — https://no-color.org/ — an informal but widely honoured convention (`tree`
  honours it). If colour carries meaning, `NO_COLOR` destroys the graph. This alone means colour
  must be redundant.
- **Monochrome / 8-colour terminals.** The safe ANSI base is 8 colours, and the actual RGB of
  each is theme-defined. You cannot rely on "blue" being any particular blue.
- **Colour vision deficiency.** ~8% of men have some form; deuteranopia/protanopia are the common
  ones. **Specific to Daniel: magenta and cyan blur together for him.** Those are ANSI 5 and 6 —
  i.e. two of only eight base colours are effectively one channel. That leaves black/red/green/
  yellow/blue/white as candidates, and red/green is the classic CVD confusion pair, and
  blue-on-black is low contrast in many themes. Realistically **3–4 reliably distinguishable ANSI
  colours**, which is fewer than the number of ADO relation kinds.

### 4.4 Conclusion for twig

Colour is a **reinforcement channel, not a carrier**, for edge kind. Recommendation: carry
relation kind in a short **text token** on the row (e.g. `parent`, `rel`, `dup`, `succ`), colour
it as a secondary cue, and use **partition + filter** (cargo-tree's approach) when several kinds
must be visible at once. Do not spend engineering effort on coloured connectors — the crossing
cell cannot represent two colours anyway, and `NO_COLOR` erases the whole scheme.

---

## 5. Interaction patterns in TUI graph/tree views

### 5.1 Focus + expand

Universal. Terminal.Gui's `TreeView` is the direct reference: `Branch.IsExpanded`,
`Branch.Expand()`, lazy `FetchChildren()` populated on first expansion, and
`TreeStyle.ExpandableSymbol` / `CollapseableSymbol` (`+`/`-` by default) rendered per row.
https://github.com/gui-cs/Terminal.Gui/blob/develop/Terminal.Gui/Views/TreeView/Branch.cs

Lazy expansion is the key architectural property for twig: ADO relation traversal is an API call
per node, so the tree must only fetch on expand. Terminal.Gui already models this correctly.

Textual's `Tree` widget is the other well-documented reference (cursor movement, expand/collapse,
`guide_depth`): https://textual.textualize.io/widgets/tree/

### 5.2 'Follow edge' navigation

Rare as an explicit feature. The common substitute is **re-root**: select a node, press a key,
and the view redraws with that node as root. `broot` does this for directories
(https://dystroy.org/broot/); `lazygit` does it for commits (select a commit → its detail
panel becomes the new context, https://github.com/jesseduffield/lazygit).

I did not find a terminal tool with true bidirectional "jump along this specific edge, then jump
back along it" navigation. If twig wants it, that is closer to invention than adoption — flag it
as unproven.

### 5.3 Breadcrumb trails

Standard in file-manager TUIs (`broot`, `ranger`, `nnn` all show the current path as a header).
For a graph, the analogue is the **path from the root to the focused node**, which is exactly what
a re-root operation destroys unless you keep a stack. `lazygit`'s panel headers and `k9s`'s
`<pod>` breadcrumb line are the closest prior art. For twig: if you re-root, you must keep an
explicit navigation stack and render it as a breadcrumb, or users get lost immediately.

### 5.4 Filtering by edge kind

`cargo tree -e normal,build,dev,features` is the cleanest prior art: relation kinds are a
**filter set on the query**, and the rendering stays a plain tree.
https://doc.rust-lang.org/cargo/commands/cargo-tree.html

This is strongly preferable to trying to draw all kinds at once. It also composes with §4: if
only one relation kind is displayed at a time, you need no per-edge visual encoding at all.

### 5.5 Nodes reachable by several paths — the central problem

A work-item relationship graph is a **general digraph**: a work item can be reached via Parent,
via Related from a sibling, and via Duplicate-Of. Prior art offers three strategies:

**(a) Draw once, mark subsequent occurrences with a reference marker.** `cargo tree` is the
reference implementation:

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
```

> "Packages marked with `(*)` have been 'de-duplicated'. The dependencies for the package have
> already been shown elsewhere in the graph, and so are not repeated. Use the `--no-dedupe`
> option to repeat the duplicates."
> — https://doc.rust-lang.org/cargo/commands/cargo-tree.html

The `(*)` marker is one ASCII token, width-safe, colour-independent, and it makes the DAG-ness
explicit without drawing a single crossing edge. **This is the best-validated pattern in the
whole survey.** `npm ls` uses the word `deduped` for the same purpose
(https://docs.npmjs.com/cli/v9/commands/npm-dedupe/).

**(b) Draw repeatedly.** `cargo tree --no-dedupe`. Honest but explodes: a diamond becomes an
exponential blowup in a deep graph, and cycles are infinite. Only viable with a depth cap.

**(c) Draw the true crossing.** `git log --graph`. Only feasible because commit DAGs are narrow
(few concurrent lanes) and the relation is uniform (one edge kind: "parent"). Work-item graphs
are wide and multi-kind — this does not transfer.

**Cycle handling** is the trap. Spectre.Console's `Tree` throws `CircularTreeException` outright
(§3.3). ADO relations *do* contain cycles (mutual Related links are trivially cyclic). twig must
run its own visited-set traversal and emit reference markers **before** the data reaches
`Spectre.Console.Tree`, or it will throw at render time on real data.

---

## 6. Recommended glyph set and fallback ladder for twig (.NET)

### 6.1 Ranked recommendation

**Use a four-part guide table in the Spectre.Console `TreeGuide` shape, light box-drawing as the
default tier, ASCII as the safe tier, and reference markers instead of crossings.** Do not build
a crossing-capable renderer.

Rationale in one line: the crossing vocabulary caps at two distinguishable edge classes and
cannot carry colour, so a multi-kind relation graph must be expressed as tree + markers + filter,
which is also exactly what every shipping tool that faced this problem chose.

### 6.2 Tier 0 — ASCII (always safe, the floor)

```
Space    "    "
Continue "|   "
Fork     "|-- "
End      "`-- "
```
All EAW=Na, all single `char`, no measurement risk in any renderer. Matches `tree --charset=ascii`
and `Spectre.Console.AsciiTreeGuide` exactly. Use `` ` `` for End, not `\` — this is the
established convention.

Reference marker: `(*)`. Cycle marker: `(cycle)` or reuse `(*)`.

### 6.3 Tier 1 — Light box-drawing (default)

```
Space    "    "     
Continue "│   "     U+2502
Fork     "├── "     U+251C U+2500 U+2500
End      "└── "     U+2514 U+2500 U+2500
```
Identical to `Spectre.Console.LineTreeGuide` and Rich's first `TREE_GUIDES` entry. Every glyph is
BMP, single `char`, universally font-covered. **Caveat: all four are EAW=Ambiguous**, so they are
double-width under a CJK-configured terminal — this tier is safe in practice, not in theory.

Optional rounded variant for the End corner (`╰──`, U+2570) if you want the jj/Sapling "curved"
look — but per §1.2, keep `├` square for Fork because a rounded tee does not exist.

### 6.4 Tier 2 — emphasis, if you want a second visual level

Use **heavy** for the "primary hierarchy" (Parent/Child) and **light** for everything else:
`┃ ┣━━ ┗━━` (U+2503, U+2523 U+2501, U+2517 U+2501). This is the only two-class distinction the
block supports cleanly at a crossing, and it works without colour. It is `BoldLineTreeGuide`.

Do **not** use the double family (`║ ╠══ ╚══`) as a third class in the same view — there is no
heavy×double junction glyph (§1.2), so any place the two families meet is undrawable.

### 6.5 Selection logic

Mirror Spectre.Console's own mechanism rather than adding a flag users must find:
1. Honour an explicit `--ascii` / `--charset=ascii` flag if given (matches `tree`, `cargo tree`).
2. Otherwise auto-detect: if `Console.OutputEncoding` is not UTF-8, or the platform/terminal is
   unknown, drop to Tier 0. This is what `cargo tree` ("cargo will auto-select a value") and Rich
   (`Console.ascii_only`) do.
3. Declare `SafeTreeGuide => Ascii` on any heavy/double guide, exactly as Spectre.Console does —
   heavy and double degrade **straight to ASCII**, not to light.
4. Honour `NO_COLOR` (https://no-color.org/) independently of the glyph tier.

### 6.6 Edge-kind encoding (the actual recommendation)

- Relation kind goes in a **short text token on the row**, not in the connector geometry and not
  in connector colour.
- Colour that token, as reinforcement only. **Avoid magenta/cyan as a distinguishing pair**
  (Daniel cannot separate them) and avoid red/green as a distinguishing pair (most common CVD).
  Practical safe set: default-fg, yellow, blue, red — four classes maximum, and each must be
  redundant with its text token per WCAG 1.4.1.
- Provide a `--relation <kinds>` filter in the `cargo tree -e` style. One kind on screen at a
  time is worth more than any glyph scheme.
- Multi-path nodes: draw once, mark `(*)`, provide `--no-dedupe` with a depth cap.

### 6.7 Explicit width-measurement warnings

**Do not use, in Spectre.Console:**

| Family | Range | Why |
|---|---|---|
| Supplementary PUA (nf-md-* Material Design Nerd Font) | U+F0001–U+F1AF0 | Surrogate pairs; measured as 0 width. Already documented in `IconSet.cs`. EAW=A. Permanent column misalignment, unfixable by padding. |
| Symbols for Legacy Computing (fine diagonals `🮠🮡🮢🮣`, `🮯`, block sextants) | U+1FB00–U+1FBEF | Same supplementary-plane surrogate hazard, **plus** near-zero font coverage (Wikipedia: "few fonts support these characters"). Double disqualification. |
| Any emoji | various | ZWJ (U+200D) sequences and VS16 (U+FE0F) change measured width by Unicode version; see spectre.console#2086 and Aspire's private `EmojiWidth` workaround table. |
| Any codepoint > U+FFFF, generally | — | `Cell.cs` caches width in an `sbyte[char.MaxValue + 1]` indexed by UTF-16 **code unit**, and the netstandard2.0 path iterates `char`. Treat >BMP as broken. |

**Use with awareness:**

| Family | Range | Note |
|---|---|---|
| Box Drawing solid/corners/tees/crosses/arcs | U+2500–U+254B, U+2550–U+2573 | **EAW=Ambiguous.** 1 cell normally, 2 cells under CJK-configured terminals. Safe in practice; not width-guaranteed. |
| Box Drawing dashes and stubs | U+254C–U+254F, U+2574–U+257F | EAW=Neutral — *different* from the solid lines. Mixing them in one guide can behave inconsistently. |
| BMP PUA (Codicons, Font Awesome, Seti, Devicons, Octicons) | U+E000–U+F8FF | Safe in Spectre.Console (single `char`), but **EAW=Ambiguous** and depends on a Nerd Font being installed. Already twig's documented policy. |

**Guaranteed-safe:** ASCII only (EAW=Na, one byte, one cell, everywhere).

**Unexplored but interesting:** DEC Special Graphics (`ESC ( 0` … `ESC ( B`, as used by Sapling's
`DEC_GLYPHS`) gives box-drawing appearance with ASCII-byte width semantics. It is almost certainly
incompatible with Spectre.Console's and Terminal.Gui's ownership of the escape stream, so treat it
as a curiosity unless twig ever writes raw to stdout.

---

## Appendix: sources

- Unicode Box Drawing chart — https://www.unicode.org/charts/PDF/U2500.pdf
- Box-drawing character (Wikipedia) — https://en.wikipedia.org/wiki/Box-drawing_character
- UAX #11 East Asian Width — https://www.unicode.org/reports/tr11/
- EastAsianWidth.txt (UCD) — https://www.unicode.org/Public/UCD/latest/ucd/EastAsianWidth.txt
- Spectre.Console tree guides — https://github.com/spectreconsole/spectre.console/tree/main/src/Spectre.Console/Rendering/Tree
- Spectre.Console `Cell.cs` — https://github.com/spectreconsole/spectre.console/blob/main/src/Spectre.Console/Internal/Cell.cs
- Spectre.Console issue #2086 (emoji width) — https://github.com/spectreconsole/spectre.console/issues/2086
- Wcwidth for .NET — https://github.com/spectreconsole/wcwidth
- jquast/wcwidth (upstream Python port) — https://github.com/jquast/wcwidth
- Terminal.Gui `TreeStyle` — https://github.com/gui-cs/Terminal.Gui/blob/develop/Terminal.Gui/Views/TreeView/TreeStyle.cs
- Terminal.Gui `Branch` — https://github.com/gui-cs/Terminal.Gui/blob/develop/Terminal.Gui/Views/TreeView/Branch.cs
- Rich `tree.py` — https://github.com/Textualize/rich/blob/master/rich/tree.py
- Rich Tree docs — https://rich.readthedocs.io/en/stable/tree.html
- Textual Tree widget — https://textual.textualize.io/widgets/tree/
- jj graph style config — https://github.com/jj-vcs/jj/blob/main/docs/config.md
- Sapling renderdag glyph tables — https://github.com/facebook/sapling/blob/main/eden/scm/lib/renderdag/src/pipeline/row_shape_to_prefix_lines/box_drawing.rs
- cargo tree — https://doc.rust-lang.org/cargo/commands/cargo-tree.html
- npm dedupe — https://docs.npmjs.com/cli/v9/commands/npm-dedupe/
- git log — https://git-scm.com/docs/git-log
- WCAG 1.4.1 Use of Color — https://www.w3.org/WAI/WCAG21/Understanding/use-of-color.html
- WCAG 1.4.11 Non-text Contrast — https://www.w3.org/WAI/WCAG21/Understanding/non-text-contrast.html
- NO_COLOR — https://no-color.org/
- Monospaced font (cell metrics) — https://en.wikipedia.org/wiki/Monospaced_font
- wezterm ambiguous-width issue — https://github.com/wez/wezterm/issues/3704
- broot — https://dystroy.org/broot/
- lazygit — https://github.com/jesseduffield/lazygit
