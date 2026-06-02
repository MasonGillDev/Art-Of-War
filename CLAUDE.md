# Project Conventions

## Architectural decisions are documented in `docs/`

When an architectural decision is made — a choice between approaches that will
shape the codebase, that future contributors would otherwise have to
reverse-engineer, or that closes off alternatives — record it as a markdown
file in `docs/`.

**One file per decision.** Filename is a short kebab-case slug describing the
subject (e.g. `persistence-model.md`, `hex-vs-square-grid.md`,
`combat-variance.md`).

**Each doc must cover:**

1. **The decision** — stated up front, in one or two sentences. What was
   chosen.
2. **Why** — the rationale. What alternatives were considered, what trade-offs
   were accepted, what constraint or asymmetry forced the call. Include the
   *losing* options and why they lost; future contributors need to know what
   was already ruled out and on what grounds.
3. **Future expansion** — how the choice leaves room to grow. What can be
   added later without disturbing what's built, what would require rework, and
   what is intentionally deferred.

Where useful, also include: acceptance tests that pin the decision down,
what gets built now vs. later, and references to relevant sections of the
top-level design doc.

## When to write one

Write a decision doc when:

- A choice rules out a defensible alternative (intents vs. resolved-event log;
  hex vs. square; tile-based vs. continuous sim).
- A trade-off is being accepted that will look wrong later without context
  (e.g. accepting a small future risk to avoid a large present cost).
- The decision constrains downstream work in a way that isn't obvious from
  reading the code.

Do **not** write one for routine implementation choices, refactors, or
anything fully recoverable from `git log` and the code itself.

## When to update one

If a later decision modifies or reverses an earlier one, update the original
doc with an addendum at the bottom (`## Update YYYY-MM-DD`) rather than
silently editing the original rationale. The history of the thinking is part
of the value.
