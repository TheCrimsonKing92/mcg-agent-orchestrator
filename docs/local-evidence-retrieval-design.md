# Local Evidence Retrieval Design

This is a cautious design only. It does not add MCP, a resource server, embeddings, background indexing, or runtime retrieval behavior.

## Goal

Give future file-access workers a way to find relevant local evidence without pushing every prior log, dogfood entry, and artifact through the prompt. Retrieval should be opt-in, read-only, provenance-heavy, and easy to disable. It should complement the current `digest.md`, `prior-task-summaries.md`, prompt budgets, and direct file pointers rather than replace them.

## Candidate Sources

- `.orchestrator-context/<goal-id>/digest.md`: current role focus, open risks, evidence pointers.
- `.orchestrator-context/<goal-id>/prior-task-summaries.md`: compact prior changed files, behavior, verification, risks, and model fit.
- `.orchestrator-context/<goal-id>/prior-task-evidence.md`: full prior verification evidence, used only when summaries are insufficient.
- `.orchestrator-handoff.md`: full handoff for file-access workers when present.
- the backlog store (`.orchestrator/backlog.db`, via `backlog-list`): open follow-ups and durable decision context.
- `DOGFOOD_LOG.md` and rotated `docs/DOGFOOD_LOG-*.md`: product friction and validation history, but only when the current task explicitly asks for historical dogfood evidence.
- Source/test files changed on the goal branch, discovered through `git diff --name-only main..HEAD` or the acceptance diff provider.

## Ranking Inputs

Start with deterministic lexical retrieval before considering embeddings:

- Task role: Tester and Reviewer should prefer verification, changed files, risks, and model fit; Developer should prefer current task scope, changed files, and recent failures; Planner/Researcher should prefer objective, constraints, and open questions.
- Explicit task terms: exact symbol names, file paths, commands, goal ids, task ids, model/provider names, and quoted error text.
- Recency within the current goal: current-task artifacts before prior task summaries, prior summaries before full evidence, active log before rotated logs.
- Evidence quality: completed tasks with passing verification outrank progress notes; operator verification outranks worker claims alone.
- Provenance closeness: goal-local artifacts outrank repository-wide logs; checked-in docs outrank generated logs unless the task is about runtime behavior.
- Risk hints: words like `failed`, `blocked`, `retry-after`, `dirty`, `uncommitted`, `large-prompt`, `budget`, and `verification` increase relevance only when they appear near role/task terms.

## Trust Boundaries

Retrieval output should never be injected as authoritative prose. It should produce a small cited worklist:

- file path
- line range or section heading
- reason it matched
- age/source class, such as `goal-local`, `active-log`, `archive`, or `source`
- whether the source is worker-authored, operator-authored, or code-authored

Workers should be told to open and verify cited files directly before acting on retrieved claims. Retrieval must not read untracked logs outside the repository/worktree roots, and it must not execute commands.

## Skip Conditions

Skip retrieval and rely on direct artifacts when:

- The current prompt plus `digest.md` and `prior-task-summaries.md` are under budget and answer the context need.
- The task names exact files or symbols that can be inspected directly.
- The task is a simple code edit, formatting change, or docs-only update with no historical dependency.
- The goal is in a fresh worktree with no completed prior tasks.
- The candidate result set would include rotated dogfood archives but no current-goal evidence.
- The worker role is Planner/Researcher/Reviewer and the retrieval source is unverified worker output.

## Failure Modes

- Stale context: old dogfood entries can describe behavior fixed by later commits.
- False precision: lexical matches can over-rank repeated boilerplate such as command templates and prompts.
- Prompt injection by evidence: worker-authored logs can contain instructions that should be treated as data, not commands.
- Budget rebound: adding retrieved snippets inline can recreate the large-prompt problem.
- Cross-goal leakage: evidence from unrelated goals can bias a worker toward old constraints.

Mitigation: return pointers, not snippets, unless the caller explicitly asks for a bounded excerpt; cap results by role; prefer current-goal sources; include source class and authorship.

## Backlog Similarity Implementation

`backlog-similar` implements the first deterministic lexical slice with an SQLite FTS5 index created in memory for each invocation. It reads the current backlog database without changing its schema and indexes every backlog item's title, body, and notes plus the objective of every Completed goal. There is no persistent index, cache, background process, or synchronization state that can become stale.

Results are pointer-first: kind, id prefix, status, title, updated date, and rank. Excerpts remain opt-in with `--excerpt` and are bounded to 200 characters. After a successful `backlog-add`, the same search prints up to three advisory pointers; it never creates links or blocks the add, and `--no-similar` disables that advisory call.

## Test Plan Before Implementation

- Unit tests for source eligibility and skip conditions.
- Ranking tests with synthetic files proving goal-local summaries outrank full evidence and logs.
- Role tests proving Tester/Reviewer rank verification and risk evidence above implementation notes.
- Staleness tests proving active log/current goal evidence outranks rotated archives.
- Injection-safety tests proving retrieved worker-authored text is labeled as data and never becomes instructions.
- Prompt-budget tests proving retrieval results are pointers by default and do not trip the large-paid-prompt guard.
- End-to-end dry-run command that prints a retrieval worklist without modifying task state or dispatching workers.

## External Design Inputs

- Retrieval-augmented generation is useful when knowledge must be updated and sourced without retraining, but retrieval quality and provenance determine whether it reduces hallucination or merely adds noise.
- Prompt caching work suggests stable context should be kept stable and dynamic evidence should move later or out of prompt; this supports pointer-first artifacts and stable prompt prefixes.
- Contextual retrieval/prompt-caching guidance points toward adding compact contextual summaries to chunks, but only after the local artifact and budget path proves insufficient.
