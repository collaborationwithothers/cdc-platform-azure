---
description: Socratic walkthrough of a merged PR; updates the learning ledger and the Obsidian architecture vault.
disable-model-invocation: true
argument-hint: "PR number or URL of a merged PR"
---

Purpose: convert a merged change into durable understanding, per AGENTS.md
(PROJECT MECHANICS > "Learning loop"). The bar is mastery, not familiarity:
Hari should be able to defend every decision in the diff in an interview.

Read docs/agents/reader-contract.md before asking questions or writing the
learning artifacts. Apply it to each question, correction, and artifact. Do
not rely on an earlier chat turn to supply the required context.

Procedure:
1. Read the PR, its issue, its diff, and the spec sections the issue links.
2. Walk the change Socratically: one question at a time, building from what the
   change does, to why this way, to what breaks if it is wrong, to what the
   alternative was and when the alternative wins. Do not lecture first; ask
   first, correct after.
3. Where the change touched a learning-ledger component (docs/blueprint.md
   section 13), probe that component hardest.
4. End with Hari's own one-paragraph summary, then classify each touched ledger
   component as "mastered" or "made to work" based on the session, honestly.
5. Output two artifacts:
   - a small docs PR appending the classification and date to the learning
     ledger entry in docs/blueprint.md (this is the only repo write this
     command may make);
   - a dated note to the Obsidian architecture vault (folder
     Azure-Portfolio/teach-notes/) with the question list, the answers Hari
     struggled on, and what to revisit.
Repo read-only otherwise. Never mark a component mastered to be kind.
