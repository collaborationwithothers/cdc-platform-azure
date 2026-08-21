# Triage labels

The engineering skills speak in five canonical triage roles. This file maps
those roles to this repo's actual labels. This repo has no inbound triage
queue: issues are born ready from /to-tickets and gated by Hari, so four of the
five roles are unused.

| Canonical role  | This repo       | Notes                                 |
| --------------- | --------------- | ------------------------------------- |
| ready-for-agent | ready-for-agent | Existing label. Hari applies it only. |
| needs-triage    | none            | Unused. No inbound triage queue.      |
| needs-info      | none            | Unused.                               |
| ready-for-human | none            | Unused.                               |
| wontfix         | none            | Unused.                               |

Rules:

- Agents never create or apply triage labels.
- The "not ready" state is the absence of ready-for-agent, not a label.
- Label authority is docs/agents/issue-tracker.md. If a skill wants a label
  this table maps to none, it takes no label action and defers to Hari.
