# Development environment

How to run implementation sessions against this repo, including several in
parallel. Actor is Hari unless a step names the agent.

## One worktree per session

Parallel Claude Code sessions must not share a working copy; checkouts and
builds would trample each other. Each session gets its own git worktree and its
own session id.

Setup, from the directory containing the main clone:

    cd cdc-platform-azure
    git worktree add ../cdc-s1 main
    git worktree add ../cdc-s2 main

Start each Claude Code session in its own worktree directory and tell it its
session id (s1, s2, ...) in the first message. The session uses that id for its
in-progress label per AGENTS.md's claim protocol.

When a ticket finishes, the session's branch is pushed and the worktree can be
reused for the next ticket after `git fetch && git switch main && git pull`.
Remove a worktree with `git worktree remove ../cdc-s1` when retiring a session.

## Local verification (containers)

The default verification method is container-based; no Azure needed. Prereqs on
the dev machine: Docker, .NET SDK, Java 17+ (for Connect tooling only if run
outside containers).

Integration tests use Testcontainers to run SQL Server, Kafka (KRaft), and
Kafka Connect. `dotnet test` from the repo root runs unit plus containers
suites; the containers suite is tagged and can be skipped with the category
filter when iterating on pure units. CI runs both on ubuntu-latest.

If a ticket declares verification method "live", stop: live tickets are
serialized and run by Hari only (AGENTS.md, Verification strategy).

## Live sessions (Hari only)

- Precondition: budget alerts deployed (persistent layer) before any
  disposable-layer apply.
- Session pattern: apply disposable layer, work, `terraform destroy` the
  disposable layer before ending the session. Destroyed is the default end
  state; a standing environment is the exception and is time-boxed.
- Destroy severs CDC enablement and Entra database users; recreation follows
  the recovery runbook (re-enable CDC, re-run identity provisioning, trigger
  incremental snapshots, queues rebuild via replay plus reconciler).
- Record actual spend in COSTS.md after each billing update.

## Parallel session etiquette

- A session never claims a second ticket while its first is unmerged, unless
  the first is parked with a comment saying why.
- Merge conflicts between areas indicate a missing blocking edge or a Paths
  violation; stop and fix the tickets, do not resolve-and-hope.
- Stacked PRs only within one area; after a parent merges, rebase the child per
  docs/agents/pr-size.md before requesting review.
