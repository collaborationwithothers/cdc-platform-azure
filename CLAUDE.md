@AGENTS.md
@docs/agents/writing-style.md

# CLAUDE.md

This file is the Claude Code entry point. Its first line imports AGENTS.md, the
single source of truth for all tool-neutral rules (governance, scope, safety,
verification, ticket workflow, parallel operation). The second line imports
docs/agents/writing-style.md so the exemplar, banned constructions, and Learned
rules are in context every session. Do not duplicate any AGENTS.md rule here.

## Claude Code bindings

### Model bindings for the tier split

- Implementation tier: Sonnet (highest available, effort high). Implementation
  sessions and the pre-PR /code-review self-check run on this model.
- Review tier: Opus (highest available, effort high). Governance review runs on
  this model, in a separate session, with Hari, never by the session that
  authored the change. If a governance review session is not on the review
  model, say so before reviewing.

### Agent identity

When Claude Code acts as a loop session it uses: branch prefix claude/,
in-progress label in-progress:{session-id} where Hari assigns the session id
(s1, s2, ...) at session start, attribution label agent:claude. If Hari has not
given a session id, ask for one before claiming any ticket.

### Commands

- /work-frontier (.claude/commands/work-frontier.md, Sonnet): runs the parallel
  frontier workflow defined in AGENTS.md as a claude session.
- /governance-review (.claude/commands/governance-review.md, Opus): runs the
  governance review workflow defined in AGENTS.md; output is for Hari and
  exempt from the reader contract.
- /teach (.claude/commands/teach.md, any model): Socratic walkthrough of a
  merged PR for Hari's learning; updates the learning ledger and the Obsidian
  architecture vault. Repo read-only apart from the ledger docs PR.

### Subagents

- azure-docs-verifier is the binding for the documentation-verification role in
  AGENTS.md's truth and verification rules. Use it for every Azure capability
  claim, SKU, limit, Debezium/Connect/Strimzi configuration claim, or auth
  setting before it goes into code or docs. If it returns UNVERIFIABLE, the
  claim does not ship.

## PROJECT MECHANICS (Claude Code: append below as you learn the codebase)

Tool-neutral mechanics live in AGENTS.md under PROJECT MECHANICS. Append only
Claude-Code-specific mechanics here.
