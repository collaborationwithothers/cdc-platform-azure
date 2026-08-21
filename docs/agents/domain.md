# Domain docs

How the engineering skills consume this repo's domain documentation. This repo
is single-context.

## Where the domain lives

- The context, glossary, and design live in docs/blueprint.md. It is the spec
  seed named in AGENTS.md ("What this repo is") and is authoritative.
- Root CONTEXT.md is a pointer stub to docs/blueprint.md, kept only so tools
  that look for a CONTEXT.md find their way. If the two ever disagree,
  docs/blueprint.md wins.
- ADRs live in docs/decisions/, numbered from the blueprint. Read the ADRs that
  touch the area you are about to work in. If the directory does not exist yet,
  proceed silently.

## Use the blueprint's vocabulary

When your output names a domain concept (an issue title, a refactor proposal, a
hypothesis, a test name), use the term as defined in the docs/blueprint.md
glossary. Do not drift to synonyms it avoids. If a concept is not in the
glossary, that is a signal: either you are inventing language the project does
not use, or there is a real gap to raise with Hari.

## Flag ADR conflicts

If your output contradicts an existing ADR in docs/decisions/, surface it
rather than silently overriding it, for example: "Contradicts ADR-0007; worth
reopening because ...".
