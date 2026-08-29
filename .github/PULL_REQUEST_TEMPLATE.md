<!-- Title: imperative, scoped, e.g. "Add outbox event router SMT config" -->

## The concept

<!-- This repository is a multi-tenant change-data-capture platform on Azure.
Each tenant's Azure SQL database is a source. Debezium reads committed changes
and publishes events to Kafka, a named stream of messages, for .NET consumers.
Explain the affected component and its plain role before naming files. Keep
the concept self-contained. This repository template is canonical and
overrides a conflicting generic skill template. Required context takes
priority over brevity. -->

**Context and affected component:**

**Why this matters:**

**Before this PR:**

**After this PR:**

**Current state:**

**Historical evidence (when this PR edits bot-authored history):**

**Unknowns:**

Closes #

## Reading order: core files first

<!-- Split every changed file into two lists. Core files carry the concept.
Supporting files carry tests, gate evidence, or documentation required by the
repository rules. Reading the core files alone must be enough to understand
the change. Do not omit a file or context to make the description shorter. -->

Core:

-

Supporting:

-

## Concrete example when behavior depends on sequence or failure

<!-- Required when this PR changes ordering, retries, concurrency, or failure
behavior. Give a concrete before-and-after sequence with actors, inputs, and
the result. Otherwise write "Not applicable: this PR does not change ordering,
retries, concurrency, or failure behavior." -->

Scenario:

Before:

After:

## Verification

<!-- The ticket's declared method and the evidence it ran. Name what the check
proves and the boundary it does not cover. -->

Method (unit / containers / live):

Evidence (test run link or command output summary):

Proven boundary:

Unverified boundary:

## Self-check

<!-- Paste the pre-PR code-review skill output (Standards and Spec axes)
here, or link the commit that recorded it. Governance review reads this
instead of rerunning the review. An empty section is a REQUEST CHANGES. -->

## PR size

Independently verified behavior:

Measured size: <!-- files; additions; deletions; additions plus deletions -->

Approved exception justification: N/A

Approved exception link: N/A

## Paths

<!-- Confirm the diff stays inside the ticket's Paths list, or state the
exception and why. -->

Within declared paths: yes / no (reason)

## Merge class

<!-- Exactly one. See AGENTS.md GOVERNANCE > Merge classes. -->

## Review summary

<!-- What changed, why, and links to the Microsoft Learn or project
documentation that justify every Azure, Debezium, Connect, Strimzi, or Kafka
configuration claim in the diff. Keep it self-contained; do not remove context
or evidence to make the summary shorter. -->

## Publication check

<!-- Render this pull request description in GitHub before submitting it.
Reread the rendered result as an Azure engineer who is new to this repository,
Kafka, and event-driven systems. Confirm that the affected component, why the
change matters, current verification state, and any unknowns are clear without
an earlier chat turn. -->

Rendered and reread: yes / no
