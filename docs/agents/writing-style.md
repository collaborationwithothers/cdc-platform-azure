# Writing style

This file is the concrete half of the "Writing standards" section in AGENTS.md.
That section holds the rules; this file holds the exemplar that shows the
target register, the banned-constructions list that reviews enforce, and the
Learned rules list that grows by the correction ratchet. Read this file before
writing any substantial doc and match the exemplar's register.

## The register: one sample, before and after

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

> The queue-builder notices when an event went missing. Every transition
> carries a version number incremented in the same database transaction as the
> change, so the sequence per task has no legitimate gaps. When the
> queue-builder sees version 7 arrive after version 5, it knows 6 was lost,
> marks that task's queue entry unreliable, and fetches current truth from
> task-api to correct it.

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
