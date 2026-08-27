---
name: handoff
description: Compact the current conversation into a handoff document for another agent to pick up.
argument-hint: "What will the next session be used for?"
disable-model-invocation: true
---

Write a handoff document summarising the current conversation so a fresh agent can continue the work. Save to the temporary directory of the user's OS - not the current workspace.

**Authoritative output contract:** Read [the repository reader contract](../../../docs/agents/reader-contract.md) before writing; apply its context, first-use terms, consequence, and truth requirements. The recipient must understand the handoff without earlier conversation or required links.

Include a "suggested skills" section in the document, naming which skills the next agent should call the Skill tool for.

Do not duplicate details already captured in other artifacts (specs, plans,
ADRs, issues, commits, diffs), but repeat the conclusion and boundaries the
recipient needs to act. References may add evidence; they cannot carry required
context.

Use this shape:

```
# Handoff: <focus>

## Current conclusion

[What is true now, why it matters, and what remains unresolved.]

## Scope

[What this session changed or investigated, and what it deliberately did not change.]

## Verification

[Checks run, their results, and any unverified boundary.]

## Blockers and unknowns

[Open dependency, decision, or evidence gap. Separate current state from historical evidence when both matter.]

## Next action

[The next agent's first concrete action and its completion condition.]

## Suggested skills

- [Skill name and why it fits the next action.]
```

Write the handoff only when every section has a concrete value and the next
agent can start without reconstructing the conclusion from a link.

Redact any sensitive information, such as API keys, passwords, or personally identifiable information.

If the user passed arguments, treat them as a description of what the next session will focus on and tailor the doc accordingly.
