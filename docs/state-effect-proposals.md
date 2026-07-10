# State-Effect Proposals

Workers must not write orchestrator SQLite stores directly. To request a durable state effect, add a proposal file to the goal diff:

`.orchestrator-proposals/<kind>-<slug>.md`

Supported kinds:

- `backlog-add`
- `backlog-close`
- `goal-record-note`

Each proposal is Markdown with required front matter:

```markdown
---
kind: backlog-add
title: Follow-up title
---
Body for the backlog item.
```

`backlog-add` fields:

- `kind: backlog-add`
- `title: <non-empty title>`
- `id: <stable id>` optional; defaults to a deterministic slug from `title`
- `sourceGoalId: <goal id>` optional; defaults to the landed goal id

`backlog-close` fields:

- `kind: backlog-close`
- `id: <backlog item id>`
- `reason: <close reason>` optional

`goal-record-note` fields:

- `kind: goal-record-note`
- `message: <note>` optional when the Markdown body is non-empty

Acceptance validates proposal filenames and schema. After a goal lands, the conductor applies landed proposals idempotently, keyed by the proposal file hash, and records the application in the goal operation journal.
