# GitHub comment shapes

These shapes keep GitHub comments understandable to an Azure engineer who is
new to this repository and to event-driven systems. The repository is a
multi-tenant change-data-capture platform on Azure: each tenant's Azure SQL
database is a source, Debezium reads committed database changes, Kafka is the
named stream that carries the resulting events, and .NET consumers process
those events. A comment records what is true at one point in that path and why
the next action matters.

The shapes below are canonical for this repository. A generic bundled skill
may offer a shorter shape, but the repository shape wins. Replace every
bracketed placeholder before posting. Keep `Current state`, `Historical
evidence`, and `Unknowns` separate when more than one applies. There is no
fixed line cap: do not remove context that a first-time reader needs.

Before posting, render the comment in GitHub and reread it without the issue,
pull request, or an earlier chat turn. A link can provide evidence, but it
cannot provide required context. Name the exact check and say what that check
does not prove.

## Pickup

Use this shape when a session starts work on an issue.

```markdown
**Context:** This repository is a multi-tenant change-data-capture platform on Azure. A tenant's Azure SQL database is the source. Debezium is a connector that reads committed database changes. Kafka is a named stream of messages. A consumer is a service that reads those messages. The affected component is **[component]**, which [plain role].

**Current state:** I am picking up issue #[number], **[title]**. The issue owns **[paths]** and is blocked by **[none or issue numbers and reasons]**.

**Work:** I will make **[independently true behavior]** true.

**Why it matters:** [Concrete consequence for the source database, event stream, consumer, operator, or reader.]

**Session:** **[session id]** on **[branch]**, started **[timestamp]**. **Next:** I will run **[declared verification]** and leave the pull request for Hari's review. I will not merge it.
```

## Progress

Use this shape when work has advanced but the issue is not complete.

```markdown
**Context:** This repository is a multi-tenant change-data-capture platform on Azure. Debezium is a connector that reads committed database changes from each tenant's Azure SQL database. Kafka is a named stream of messages. A consumer is a service that reads those messages. The affected **[component]** is the service or process that [plain role].

**Current state:** **[What is now true, with the exact component and boundary.]**

**Why it matters:** **[What this progress protects or makes possible.]**

**Historical evidence:** **[Earlier check, commit, or dated result that explains the change. Write `None` when there is none.]**

**Unknowns:** **[What remains unverified, or `None`.]**

**Next:** **[One concrete next action and its verification.]**
```

## Blocker

Use this shape when a decision, dependency, or failed check stops the work.

```markdown
**Context:** This repository is a multi-tenant change-data-capture platform on Azure. The affected path is **[source database -> Debezium -> Kafka topic -> consumer]**. Debezium is a connector that reads committed database changes. Kafka is a named stream of messages. A consumer is a service that reads those messages.

**Blocker:** **[One plain statement of the missing decision, dependency, or failed precondition.]**

**Evidence:** **[Exact command result, issue or PR state, and date. Link: [URL or `None`].]**

**Impact:** Work on **[issue and paths]** cannot proceed because **[specific consequence]**. The branch remains **[branch]**. Product or cloud state: **[none changed, or exact current state]**.

**Required action:** **[The one decision or action needed, naming the owner.]**

**Unknowns:** **[What is still not known, or `None`.]**
```

## Verification

Use this shape when reporting a check without claiming more than it proves.

```markdown
**Context:** This repository moves committed changes from a tenant's Azure SQL database through Debezium. Debezium is a connector that reads committed database changes. Kafka is a named stream of messages. A consumer is a service that reads those messages. The affected **[component]** is the consumer service that [plain role].

**Check:** **[unit / containers / live]** - **[exact command or live check]**.

**Current state:** **[Pass or fail, the observed result, and the component boundary reached.]**

**Why it matters:** **[The failure or protection this result identifies.]**

**Boundary:** This check proves **[specific behavior]**. It does not prove **[unexercised integration, production scale, live Azure behavior, or other limit]**.

**Unknowns:** **[Remaining uncertainty, or `None`].** **Next:** **[Follow-up check or owner].**
```

## Completion

Use this shape when the implementation and its declared verification are ready
for review.

```markdown
**Context:** This repository is a multi-tenant change-data-capture platform on Azure. Debezium is a connector that reads committed database changes from a tenant's Azure SQL database. Kafka is a named stream of messages. A consumer is a service that reads those messages. The change is in **[component]**, which [plain role].

**Current state:** PR #[number], **[title]**, makes **[behavior]** true. This matters because **[concrete consequence]**.

**Acceptance:** **[How each acceptance item is evidenced, or the exact item that remains open.]**

**Verification:** **[Method, command or check, result, and boundary.]**

**Paths and scope:** The diff stays within **[paths]**. Out of scope: **[items]**.

**Historical evidence:** **[Useful dated prior state preserved by this change, or `None`].**

**Unknowns:** **[Unverified behavior, or `None`].** **Review handoff:** **[What Hari should inspect first].**
```

## Handoff

Use this shape when passing an issue or pull request to another session or to
Hari. It carries the conclusion and next action without requiring a link.

```markdown
**Context:** This repository is a multi-tenant change-data-capture platform on Azure. Debezium is a connector that reads committed database changes from a tenant's Azure SQL database. Kafka is a named stream of messages. A consumer is a service that reads those messages. The handoff concerns **[component]**, which [plain role].

**Current conclusion:** **[What is now true, what remains, and why it matters.]**

**Scope:** **[Issue or PR, owned Paths, and explicit out-of-scope items.]**

**Verification:** **[Method, exact command or check, result, and proven boundary.]**

**Blockers:** **[Open dependency or decision, or `None`].**

**Unknowns:** **[Unverified behavior or missing evidence, or `None`].**

**Next:** **[One concrete action, its owner, and the condition for completion.]**
```

## Takeover

Use this shape only after the abandonment rule permits a session to reclaim a
ticket. Preserve the previous session's useful evidence.

```markdown
**Context:** This repository is a multi-tenant change-data-capture platform on Azure. Issue #[number] changes **[component]**, a consumer service that reads messages and [plain role], on the path from a tenant's Azure SQL database through Debezium to Kafka. Debezium is a connector that reads committed database changes. Kafka is a named stream of messages. A consumer is a service that reads those messages.

**Historical evidence:** The previous session **[session id]** recorded **[branch, last commit, dated verification, and PR link]**. That evidence remains **[valid / invalid because ...]**.

**Current state:** The previous session has had no branch push for **[duration]**, so the 48-hour abandonment rule permits this takeover. I am continuing the same Paths list: **[paths]**. I am not changing the issue's scope.

**Why it matters:** **[Consequence of completing the existing work.]** **Next:** I will first reread **[diff or verification]**, then run **[check]** and report any changed boundary.

**Unknowns:** **[Unresolved questions, or `None`].**
```

## Historical rewrite

Use this shape when replacing technically editable model-produced GitHub
history authored by `haripraghash-bot`. Do not use it to rewrite human-authored
content, existing commit messages, or chat history.

```markdown
**Context:** This repository is a multi-tenant change-data-capture platform on Azure. Debezium is a connector that reads committed database changes from a tenant's Azure SQL database. Kafka is a named stream of messages. A consumer is a service that reads those messages. The rewritten artifact concerns **[component]**, which [plain role].

**Current state:** The replacement now says **[self-contained current result and consequence]**.

**Historical evidence:** On **[date]**, the bot-authored artifact said **[short useful fact from the old text]**. It was replaced because **[specific missing context, stale claim, or mixed truth state]**. The dated fact is preserved here: **[fact]**.

**Why it matters:** **[How the clearer or corrected artifact helps a reader or operator.]**

**Unknowns:** **[What the historical record cannot establish, or `None`].**
```
