---
name: to-spec
description: "Turn the current conversation into a spec and publish it to the project issue tracker: no interview, just synthesis of what you've already discussed."
disable-model-invocation: true
---

This skill takes the current conversation context and codebase understanding and produces a spec. Do NOT interview the user; just synthesize what you already know.

**Authoritative output contract:** Read [the repository reader contract](../../../docs/agents/reader-contract.md) before writing; apply its context, first-use terms, consequence, and truth requirements. Links provide evidence but never required context.

The issue tracker and triage label vocabulary should have been provided to you. If not, tell the user to run `/setup-matt-pocock-skills`.

## Process

1. Explore the repo to understand the current state of the codebase, if you haven't already. Use the project's domain glossary vocabulary throughout the spec, and respect any ADRs in the area you're touching.

   Done when the relevant current behavior, domain terms, and decisions are recorded.

2. Sketch out the seams at which you're going to test the feature. Existing seams should be preferred to new ones. Use the highest seam possible. If new seams are needed, propose them at the highest point you can. The fewer seams across the codebase, the better - the ideal number is one.

Check with the user that these seams match their expectations.

   Done when the user has confirmed the seams or the conversation already contains that decision.

3. Write the spec using the template below, then publish it to the project issue tracker. Leave the `ready-for-agent` label absent unless Hari explicitly authorises it; only then may it be applied.

   Done when the issue is published with every repository governance section, no unresolved placeholder, and the exact approved behavior and verification evidence.

<spec-template>

## Behavior

### Problem Statement

The problem that the user is facing, from the user's perspective. Name the
system context, define central unfamiliar terms, and state why the problem
matters.

### Solution

The solution to the problem, from the user's perspective.

### User Stories

A LONG, numbered list of user stories. Each user story should be in the format of:

1. As an <actor>, I want a <feature>, so that <benefit>

<user-story-example>
1. As a mobile bank customer, I want to see balance on my accounts, so that I can make better informed decisions about my spending
</user-story-example>

This list of user stories should be extremely extensive and cover all aspects of the feature.

### Implementation Decisions

A list of implementation decisions that were made. This can include:

- The modules that will be built/modified
- The interfaces of those modules that will be modified
- Technical clarifications from the developer
- Architectural decisions
- Schema changes
- API contracts
- Specific interactions

Do NOT include specific file paths or code snippets. They may end up being outdated very quickly.

Exception: if a prototype produced a snippet that encodes a decision more precisely than prose can (state machine, reducer, schema, type shape), inline it within the relevant decision and note briefly that it came from a prototype. Trim to the decision-rich parts, not a working demo, just the important bits.

### Testing Decisions

A list of testing decisions that were made. Include:

- A description of what makes a good test (only test external behavior, not implementation details)
- Which modules will be tested
- Prior art for the tests (i.e. similar types of tests in the codebase)

### Further Notes

Any further notes about the feature.

## Blocked by

None (can start immediately), or list each issue that genuinely blocks this behavior.

## Paths

- [Owned path, determined from codebase exploration.]

## Verification

Method: unit / containers / live

[Name the concrete command, test, or live check that verifies the behavior.]

## Acceptance checklist

- [ ] [A checkable user-visible outcome.]
- [ ] [A checkable verification result.]

## Out of scope

A description of the things that are out of scope for this spec.

## Size forecast

Expected files and additions plus deletions.

</spec-template>
