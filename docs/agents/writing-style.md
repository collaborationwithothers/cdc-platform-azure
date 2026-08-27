# Writing style

This file is the concrete half of the "Writing standards" section in AGENTS.md.
The first-time reader contract in docs/agents/reader-contract.md defines the
reader and the required context. This file holds the exemplar that shows the
target register, the banned-constructions list that reviews enforce, and the
Learned rules list that grows by the correction ratchet. Read both files before
writing any substantial doc. The contract decides what the reader must know;
this file decides how to say it.

## The register: one sample, before and after

The After paragraph follows the first-time reader contract: it names the
component's place in the system, explains its terms, and shows the operational
consequence. The Before paragraph intentionally shows a failure of both the
writing register and the first-time reader contract.

Both paragraphs below describe the same feature and contain the same facts.
The first is the register agents drift into by default. The second is the
register this repo requires.

Before (do not write like this):

> This document provides a comprehensive overview of the implementation of gap
> detection functionality within the queue-builder component. It should be
> noted that detection capability is facilitated through the utilization of a
> monotonically incremented version attribute, which enables the
> identification of discontinuities in the event sequence. In order to ensure
> that projection integrity is maintained in a robust manner, a repair
> mechanism has been implemented that performs retrieval of authoritative
> state from the source system.

After (write like this):

> The queue-builder is the service that reads workflow transitions from Kafka,
> a named stream of messages, and writes the work-queue projection, its copy of
> task data for fast reads. A workflow transition is a task state change
> carried in an event, a message describing that change. The queue-builder
> notices when an event went missing. Every transition carries a version number
> incremented in the same database transaction as the change, so the sequence
> per task has no legitimate gaps. When the queue-builder sees
> version 7 arrive after version 5, it knows 6 was lost, marks that task's queue
> entry unreliable, and fetches current truth from task-api, the service that
> exposes source workflow state, to correct it.

Why the second one works:

- It opens with what the thing does and why it matters, in plain words. The
  "before" paragraph opens by describing itself.
- Every verb is doing work: notices, carries, sees, knows, marks, fetches. The
  "before" version hides those actions inside nouns (implementation,
  utilization, identification, retrieval).
- Same facts, fewer words, and nothing lost. Simplifying is cutting ceremony,
  not cutting content.

## Banned constructions

Using one of these in a doc, PR description, review summary, ADR, or comment is
a review finding. The replacement is always shorter.

| Banned | Write instead |
| --- | --- |
| it should be noted that / it is important to note | (delete; just say the thing) |
| in order to | to |
| utilize, utilization | use |
| leverage (as a verb) | use |
| facilitate, enable (when the subject just does the thing) | the plain verb: does, runs, checks |
| comprehensive, robust, seamless, cutting-edge | (delete; empty praise proves nothing) |
| functionality, capability (as filler) | name the behaviour |
| the implementation of X was performed | X was implemented, or better: we/it implemented X |
| as mentioned above / as previously discussed | restate the fact in half a sentence, or link the section |
| passive voice that hides who acts, when the actor matters | name the actor: "the connector retries", not "a retry is performed" |

The list is a floor, not a ceiling: prose can avoid every entry and still be
robotic. When in doubt, reread the exemplar.

## Learned rules

Grown by the correction ratchet in AGENTS.md ("Writing standards"): every time
Hari corrects wording, tone, or structure in a session, the agent appends one
line here capturing the general rule behind the correction. One line per rule,
dated, newest last. Rules here carry the same weight as the AGENTS.md rules
above them. Seeded from the sibling repo's repo-neutral rules.

- 2026-08-08: If Hari has to ask "explain what this says" or "simplify this",
  the document failed; fix the document, do not just answer in chat.
- 2026-08-10: If Hari says he doesn't understand and to treat him as new to
  the repo/session, that is not a request to compress further; drop jargon,
  define terms plainly, and rebuild the explanation from the ground up, even
  if it runs long.
- 2026-08-10: A document that has accumulated dated corrections over time
  should separate CURRENT STATE from HISTORICAL EVIDENCE into distinct
  sections, not interleave dated patches into the procedure a reader follows
  today.
- 2026-08-11: Name repo concepts with everyday words. When a plainer word
  carries the same meaning, the plain word wins; a coined or borrowed
  technical metaphor in a heading or rule is a review finding.
- 2026-08-11: For a manual command procedure, start with the exact working
  directory and show how to obtain every required input before the command
  that uses it.
- 2026-08-12: Describe the mechanic in one concrete sentence before naming a
  decision about it; a reader who cannot picture the thing cannot make the
  call.
- 2026-08-18: Ask for one decision in plain words. Do not hide the decision
  inside a list of technical checks the reader must decode first.
- 2026-08-21: Domain vocabulary (projection, replay, rebalance, consumer)
  stays, but every such term gets a one-sentence plain definition at first
  use, and the glossary in docs/blueprint.md is the shared reference. Naming a
  service after its plumbing verb instead of the domain it owns is a finding.
- 2026-08-22: Never assume Hari holds a mental model of the project
  between sessions. When asking him for a decision or explaining a
  concept, first rebuild context in two or three plain sentences (what
  the thing is, where it sits in the system), then ground the
  explanation in one concrete worked example (a specific task, tenant,
  or timeline), then ask the decision in plain words. This applies in
  chat output, not just docs.
- 2026-08-22: Do not name a repo concept after a physical metaphor when a
  literal word exists. "lane" for a path-ownership group becomes "area";
  "seam" for the place a test drives the system becomes "test boundary";
  "own vehicle" for the top isolation rung becomes "dedicated
  infrastructure". A metaphor makes the reader translate before they can
  think, and the literal word is usually no longer.
- 2026-08-22: When a doc records a configuration setting, show what the
  setting changes about the output, not just its name and an abstract
  consequence. Two short before-and-after blocks beat a sentence naming
  the effect. This sharpens the 2026-08-12 rule for the specific case of
  config: "schemas disabled, so the topic carries the plain payload
  rather than a schema envelope" names a decision the reader cannot
  picture, and a reader who cannot picture it cannot review it.
- 2026-08-22: An ADR reference is a pointer, not an explanation. Writing
  "X is never captured by CDC (ADR-001)" makes the reader stop, find the
  ADR, and reconstruct the argument before they can judge the sentence.
  Restate the reasoning in one or two sentences and cite the ADR for the
  full version. The same applies to "blueprint section 9 makes this so":
  say what section 9 makes so.
- 2026-08-22: When a decision has a counterargument a reviewer will
  reach on their own, write the counterargument down and say why the
  decision stands anyway. An unstated alternative reads as an unnoticed
  one, and the reviewer then has to establish whether it was considered
  before they can trust anything else on the page.
- 2026-08-23: In a worked timeline, say how many of the thing there are
  before the table, and group sub-steps visibly under the step they
  belong to. A reader counts rows as events. A four-row table of "sweep
  1 pass one, sweep 1 pass two, sweep 2 pass one, sweep 2 pass two" was
  read as four sweeps, and every question that followed was built on
  that. Prose headings per unit beat a repeated column value.
- 2026-08-23: A finding about ordering, timing, or retention does not
  survive being written as prose. Lead with the concrete sequence, a few
  lines carrying real clock times and real version numbers, and put the
  supporting documentation quote after it. The V4 outcome first said "a
  transaction still in flight is not the last committed one, so its
  version must land above a watermark already handed out", which is
  accurate and still had to be explained again in chat before it landed.
  The four-line timeline that replaced it needed no explanation. The same
  applies to a negative finding: show the call, what a reader expects
  back, and what actually comes back, one above the other.
- 2026-08-26: Build multi-paragraph commit messages from separate message
  arguments, then inspect the rendered commit before pushing; never encode
  paragraph breaks as escaped text.
- 2026-08-26: When two mechanisms can produce the same observable state, say only
  that; do not call the mechanisms equivalent unless the source establishes it.
- 2026-08-26: Explain Change Tracking as changed primary keys plus versions, not
  row history, before introducing watermarks, retention, or snapshot isolation.
- 2026-08-26: A diagram for engineers new to the repository must lead with
  everyday labels and explain technical names only where the name is needed
  to operate or verify the system.
- 2026-08-27: A pull request size limit never justifies compressing reader-facing
  prose until its reasoning or evidence boundary becomes unclear. Split the work
  or use a Hari-approved exception instead.
- 2026-08-27: A test proves only the path it exercises. Name that boundary in the
  write-up before stating any broader production-scale or design-scale risk.
