#!/usr/bin/env bash
# Codex non-interactive loop shim: sequential FALLBACK for when Claude Code usage
# is exhausted. Invoked manually by the operator; NEVER run concurrently with the
# Claude Code loop (AGENTS.md, "Dual-agent operation").
#
# Thin shim only. All process text lives in AGENTS.md; this script just launches
# `codex exec` with the prompt file and the flags a non-interactive run needs.
#
# Why the flags are here and not in .codex/config.toml: the committed config is a
# conservative baseline for INTERACTIVE use. The loop reconciles it by overriding
# to a permissive posture ONLY for this non-interactive invocation, so an
# unattended run never blocks on an approval prompt:
#   -s workspace-write ............ the agent may edit files in the workspace
#   -c approval_policy=never ...... never pause for human approval (Codex docs:
#                                   use "never" for non-interactive runs)
#   -c sandbox_workspace_write.network_access=true ... gh needs network egress
#
# Model: intentionally NOT pinned here (no -m/-c model). The implementation
# model resolves to the operator's ~/.codex/config.toml default, else Codex's
# built-in default (the latest frontier coding model). This is deliberate: Codex
# model slugs churn (governance truth rules), so an unpinned loop auto-tracks the
# best available coding model with no rot to maintain, and Codex's only automated
# role is this implementation loop (governance review is Claude/Opus-only). An
# operator who wants a fixed model can add `-m <slug>` to the exec line below or
# set `model` in ~/.codex/config.toml. See COMPATIBILITY.md (Codex/Claude interop
# freshness) for the quarterly re-verify.
#
# Usage: .codex/loop.sh
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROMPT_FILE="$REPO_ROOT/.codex/prompts/work-frontier.md"

exec codex --profile github-bot \
  --cd "$REPO_ROOT" \
  -s workspace-write \
  -c approval_policy=never \
  -c sandbox_workspace_write.network_access=true \
  - < "$PROMPT_FILE"
