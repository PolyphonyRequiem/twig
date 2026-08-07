# Prior art: ambient session state addressed across process boundaries

**Question.** twig keeps the active work item as one row (`active_work_item_id`,
`src/Twig.Infrastructure/Persistence/SqliteContextStore.cs`), shared by the CLI and the MCP surface,
touched at 47 sites / 28 files. Replacing it with a per-caller **Context** forces a contract
decision: *how does a caller name which Context it is in?* This memo collects how real tools answer
that, and what breaks when the name goes stale.

**Evidence tags.** `[M]` measured on this box (versions below) · `[C]` cited, URL given ·
`[A]` asserted, my reading, no source. Where I could not find a source I write **not found** rather
than reason it out.

Local versions `[M]`: git 2.43.0, Docker 29.6.1, tmux 3.4, gh 2.94.0.
Not installed here (so no `[M]` rows for them): kubectl, terraform, aws/aws-vault, direnv.

---

## Summary table

| Tool | (a) Identity | (b) Stored where | (c) Stale / forgotten | (d) Regret evidence |
|---|---|---|---|---|
| kubectl | context *name* | file: `~/.kube/config` `current-context:` key; `KUBECONFIG` env selects/merges *which files* | **Silently acts on wrong cluster.** No per-shell isolation: `use-context` mutates the shared file, so every existing shell flips too `[C]` | Strong. An entire ecosystem (`kubectx`, `kubens`, `kube-ps1`) exists to make the ambient value switchable and *visible* `[C]` |
| docker context | context *name* | file: `~/.docker/config.json` (`currentContext`) + `~/.docker/contexts/meta/<sha>/meta.json`; `DOCKER_CONTEXT` env overrides; `DOCKER_HOST` overrides the stored context `[C]` | **Fails loud** on unknown name `[M]`; but a leftover `DOCKER_HOST` silently wins over `docker context use` `[C]` | Moderate: the `DOCKER_HOST`-beats-context precedence is a recurring StackOverflow/buildx confusion `[C]` |
| AWS_PROFILE / aws-vault | profile *name* | env var `AWS_PROFILE`; profiles in `~/.aws/config`, `~/.aws/credentials`; aws-vault stores creds in OS keychain and *injects* env into a child process | Missing → silently falls back to `default` profile / instance role `[A]`. Stale exported value → wrong account, silent | Weak-to-moderate documented. Ecosystem advice is "always run `aws sts get-caller-identity` first" `[C]` — a verification ritual is itself the tell. Named public postmortem: **not found** |
| gh auth switch | account (user) per host | file: `~/.config/gh/hosts.yml`, `users:` map + active user; token in keyring `[M]` | Global-per-machine. Every shell and every repo uses the active account; wrong-account pushes are a documented pitfall `[C]`. Does **not** set `GITHUB_TOKEN` `[C]` | Yes: guides exist purely to bolt per-directory switching on via direnv `[C]` |
| terraform workspaces | workspace *name* | file: `.terraform/environment` in the working dir; `TF_WORKSPACE` env override | Silently applies to the wrong state → destroys/mutates wrong environment `[A]`; HashiCorp itself advises against workspaces for environments `[C]` | Strong: "workspaces are a trap" writeups; HashiCorp's own guidance steers to separate configurations `[C]` |
| git worktree | filesystem *path* of the worktree (the caller's cwd) | directory: `$GIT_DIR/worktrees/<name>/` admin files; a `gitdir` file points back at the checkout | **Fails safe.** Delete the checkout and the entry is marked `prunable`; `git worktree prune` removes it `[M]`. `gc` prunes with `--expire 3.months.ago` (`gc.worktreePruneExpire`) `[M]` | Low. This is the healthiest model in the set: identity is the caller's own location, staleness is detectable and garbage-collected |
| tmux | session name/id, plus socket path | env var `$TMUX` = `<socket-path>,<server-pid>,<session-id>`; server socket under `/tmp/tmux-$UID/default` | **Acted on the wrong target in my test** `[M]`: `TMUX=/tmp/bogus,999,0 tmux new-session -d -s p3` exited 0 and created the session on the *bogus* socket, invisible to plain `tmux ls`. Bogus PID/session-id were not validated | Moderate: nesting protection exists, and `update-environment` machinery exists to re-sync stale env `[C]` |
| ssh-agent | socket path | env var `SSH_AUTH_SOCK` (path to a unix socket) | **Fails loud** `[M]`: `SSH_AUTH_SOCK=/tmp/nonexistent.sock ssh-add -l` → `Error connecting to agent: No such file or directory` | Strong ecosystem workaround: everyone symlinks a stable `~/.ssh/ssh_auth_sock` because the real path dies with the connection, breaking detached tmux `[C]` |
| direnv | the *current directory* | file: `.envrc` in the dir tree; authorization state in direnv's allow-store; requires an explicit `direnv allow` | Unauthorized/edited `.envrc` → refuses to load and prints a warning (fail loud, fail closed) `[C]`. Env is applied as a *diff* and unloaded on leaving the dir `[C]` | Low on the addressing model itself. Complaints are about hook installation and leakage into editors/IDEs, not about the directory-as-identity choice `[C]` |

---

## Measurements (run here)

```
$ DOCKER_CONTEXT=nonexistent-ctx docker ps
Failed to initialize: unable to resolve docker endpoint: context "nonexistent-ctx": context not found
```
→ docker fails loud on a stale/unknown context name. `[M]`

```
$ TMUX=/tmp/bogus,999,0 tmux new-session -d -s p3 ; echo rc=$?
rc=0
$ tmux ls
no server running on /tmp/tmux-1000/default
$ tmux -S /tmp/bogus ls
p2: 1 windows   p3: 1 windows   p4: 1 windows
```
→ tmux honoured the socket path from a *fabricated* `$TMUX` and silently operated on a different
server. The pid and session-id fields were not validated. This is the exact defect class under
study: **stale ambient reference → silent action on the wrong target.** `[M]`

```
$ SSH_AUTH_SOCK=/tmp/nonexistent.sock ssh-add -l
Error connecting to agent: No such file or directory
```
→ fails loud. `[M]`

```
$ git worktree add /tmp/wt-a -b a ; rm -rf /tmp/wt-a
$ git worktree list
/tmp/wt-a  ae2c97c [a] prunable
$ git worktree prune -v
Removing worktrees/wt-a: gitdir file points to non-existent location
```
→ abandoned context is *detected*, *labelled* (`prunable`), and reclaimable. `[M]`

```
$ man git-config | grep -A4 gc.worktreePruneExpire
gc.worktreePruneExpire
    When git gc is run, it calls git worktree prune --expire 3.months.ago.
    ... "now" may be used to disable the grace period ... "never" ...
```
→ default grace period is **3 months**; the value is configurable at both ends. `[M]`
Note the asymmetry `[M]`: an explicit `git worktree prune` with no `--expire` removed the entry
*immediately* because the `gitdir` target was gone — the grace period protects worktrees on
unmounted/removable media, not worktrees whose checkout is provably deleted. `git worktree lock`
opts a worktree out of pruning entirely `[C]`.

```
$ gh auth status
github.com  ✓ Logged in to github.com account PolyphonyRequiem (keyring)
  - Active account: true
$ ls ~/.config/gh   →  config.yml  hosts.yml   (hosts.yml has a `users:` map)
```
→ gh's active account is one global key in one file, exactly the shape twig has today. `[M]`

---

## Notes per tool

**kubectl.** `KUBECONFIG` names *files*, not a session; the active context is a mutable key inside a
file shared by every process on the machine `[C]`. The interesting evidence is negative space: the
popularity of `kubectx`/`kubens` (switching) and `kube-ps1` (rendering the current context into the
prompt) is a market signal that the ambient value is (i) awkward to change and (ii) dangerous when
invisible `[C]`. Field guidance goes further — wrap prod contexts in a script that prints the cluster
name in red and makes you type it back before a destructive verb `[C]`. That is a human-in-the-loop
confirmation gate invented because the tool has none.
- https://github.com/ahmetb/kubectx
- https://devopsaitoolkit.com/blog/managing-multiple-kubernetes-clusters-without-losing-track/
- https://kubernetes.io/docs/tasks/debug/debug-cluster/troubleshoot-kubectl/
A specific public postmortem naming "stale kubectl context deleted the wrong cluster": **not found**
in this search budget.

**docker context.** Cleanest precedence chain of the set: `DOCKER_HOST` > `DOCKER_CONTEXT` >
stored `docker context use` `[C]`. Env-over-file is the right default; the trap is a *third* legacy
env var outranking the new mechanism, which produces "I ran `docker context use` and nothing
happened" `[C]`.
- https://docs.docker.com/engine/manage-resources/contexts/
- https://docs.docker.com/reference/cli/docker/
- https://stackoverflow.com/questions/68120970/docker-context-not-changing-docker-context-use

**AWS_PROFILE / aws-vault.** Pure env-var addressing, no persisted "current profile". aws-vault's
design is notable: it does not mutate global state at all — it spawns a *child process* with
credentials injected, so the context lives and dies with that process tree `[A]`, which is the
closest analogue to "a disposable place to stand". The ritual "always check `sts get-caller-identity`
before an account-wide operation" `[C]` is the workaround-as-evidence.
- https://github.com/william-liebenberg/aws-inventory (documents the check-first ritual)

**gh.** Multi-account arrived in v2.40.0 `[C]`. It is machine-global, not per-shell or per-repo, and
it does not touch `GITHUB_TOKEN`, so an exported token silently overrides the switch `[C]`. The
ecosystem answer is to hook direnv and set the account per directory `[C]` — i.e. bolt
*location-addressing* onto a tool that only offers *global-addressing*.
- https://cli.github.com/manual/gh_auth_switch
- https://cmaven.github.io/en/git/gh-auth-switch-multi-account/ (pitfall: "Switched, but still pushes as old account")
- https://gist.github.com/git-pi-e/03de374a8e3f78e4fc5b644c89cfad20 (direnv-per-directory workaround)
- https://authsome.ai/blog/managing-multiple-github-accounts-for-ai-agents ("does not update GITHUB_TOKEN")

**terraform workspaces.** Identity is a name in `.terraform/environment` under the working directory,
overridable by `TF_WORKSPACE`. Criticism is well-attested and includes HashiCorp's own docs steering
users away from workspaces-as-environments `[C]`.
- https://developer.hashicorp.com/terraform/language/state/workspaces
- https://medium.com/@ruipmduartept/terraform-community-editionworkspaces-are-a-trap-a-platform-engineers-guide-to-scalable-iac-a1e5c38f8e3c
- https://www.reddit.com/r/Terraform/comments/v23zck/why_does_hashicorp_advise_against_using/

**git worktree.** The most directly applicable prior art for twig's *reclamation* question, not its
*addressing* question. Three primitives worth stealing wholesale: a listable registry
(`git worktree list`) that labels dead entries `prunable`; an explicit reaper (`prune`) with
`--dry-run`; a tunable expiry with a documented default (3 months) plus escape hatches `now` /
`never`; and an opt-out (`lock`) for contexts that are legitimately idle. `[M]` `[C]`
- https://git-scm.com/docs/git-worktree

**tmux.** `$TMUX` is a *composite* reference: socket path + server pid + session id. My measurement
shows tmux trusts the socket path and ignores the other two fields for validation `[M]`. Design
lesson for twig: if the handle encodes a target, validate every field, or the extra fields are
decoration that gives false confidence. tmux's `update-environment` option exists specifically to
refresh env vars that go stale across attach/detach `[C]`.
- https://gist.github.com/bcomnes/e756624dc1d126ba2eb6

**ssh-agent.** Socket-path-as-identity. Fails loud `[M]`, which is good, but the path is
*ephemeral* (dies with the ssh connection), so the universal workaround is a stable symlink that
long-lived processes point at while the real socket rotates underneath `[C]`. Direct analogue: if
twig's handle is a path or an id that dies with a process, long-lived MCP servers will need an
indirection layer, and users will invent one if twig does not ship one.
- https://werat.dev/blog/happy-ssh-agent-forwarding/
- https://www.revsys.com/tidbits/ssh_auth_sock-tmux-and-you/

**direnv.** Identity is the caller's *current directory* — nothing to forget, nothing to go stale,
because the reference is recomputed from the caller's position on every prompt. Loading is
fail-closed: content changes revoke authorization until `direnv allow` is re-run `[C]`. It applies
env as a *diff* and reverses it on leaving `[C]` — enter/exit symmetry, which is exactly the
open/close lifecycle twig wants.
- https://direnv.net/
- https://github.com/direnv/direnv
- https://man.archlinux.org/man/direnv.1.en

---

## What the pattern says (asserted)

All `[A]` — my synthesis, not sourced:

1. **Silent-wrong-target correlates with "one mutable global name in a shared file."** kubectl,
   terraform, gh. All three grew ecosystems whose only job is to make the invisible visible or the
   global local.
2. **Fail-loud correlates with "the reference is a resource handle, not a name."** docker's context
   lookup and ssh-agent's socket both fail immediately because resolution touches something that
   either exists or does not. A row in a table always resolves — that is why twig's current design
   cannot fail loud.
3. **The safest identity is one the caller already has and cannot forget:** its own directory
   (direnv, git worktree) or its own process tree (aws-vault). Nothing to pass, nothing to stale.
4. **If the handle is explicit, validate every field of it.** tmux's `$TMUX` carries a pid it never
   checks; twig should not ship a handle whose embedded metadata is decorative.
5. **Ship the reaper with the feature, not after.** git worktree's `list`/`prune`/`--dry-run`/
   `gc.worktreePruneExpire`/`lock` set is a complete, copyable design for reclaiming abandoned
   contexts, including the default-grace-period question twig will have to answer.

Direct implication for twig `[A]`: the precedent in-repo is a single env var
(`TWIG_MCP_TOOL_PROFILE`, `src/Twig.Mcp/Services/McpToolCatalog.cs:20`). An env-var handle matches
docker's `DOCKER_CONTEXT` and inherits its good property (child processes inherit; unknown values
fail loud) — *provided* resolution of an unknown/expired context id is an error, never a silent
fallback to a default or last-used context. That fallback is the single behaviour every regretted
tool in this table shares.
