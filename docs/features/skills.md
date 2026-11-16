# Installed help and skills

`twig --help`, group/leaf help and `twig --help-all` describe the actual
registered catalog offline. Syntax/defaults come from declarations; behavior
and examples come from bundled current command Markdown. Removed routes are not
promoted by old documentation.

`twig --skill` prints the executable-matched generic Twig foundation; use
`twig --skill operations`, `changes` or `presentation` for its references.
The maintained sources are `.github/skills/twig/`. No provider/workflow or
presentation adapter is installed by ordinary command execution.

Skill lifecycle commands require explicit provider and skills-root target.
Installation, host discovery/loading and user companion selection are separate
actions. The root is a directory of flat sibling skills, not a `SKILL.md` file.

```sh
twig skills install --provider copilot --target <absolute-skills-root>
twig skills status --provider copilot --target <absolute-skills-root> -o json
twig skills update --provider copilot --target <absolute-skills-root>
```

Confirm supported provider spellings with installed help. Generic targeting of
a host does not ship an adapter for that host. The old OMP proposal presenter,
Discord review helper and Copilot item-companion extension are retired; do not
link or install them from historical paths.

Installation identity includes executable/package content evidence. Updates are
explicit; edited or conflicting files must not be silently overwritten.
Separately supplied skills need review and a distinct name/root where needed;
duplicate names can shadow rather than compose. An installed file is not proof
the host trusts, enables or loads it.
