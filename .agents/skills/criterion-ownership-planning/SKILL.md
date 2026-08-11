---
name: criterion-ownership-planning
description: Plan every acceptance criterion with a feasible evidence owner, owning seam, verification class, required evidence, and stop condition.
---

# Criterion Ownership Planning

Use this procedure for Planner tasks.

For every criterion, record:

- disposition and plan;
- evidence owner, after checking role feasibility against `docs/role-capability-matrix.md`;
- owning code seam or external contract;
- verification class: `TEST-VERIFIABLE` or `REAL-WORLD-DEPENDENT`;
- required evidence; and
- stop or escalation condition.

Route full-suite, test-host, and acceptance evidence that workers cannot produce to Acceptance. Route live-system and post-landing evidence to the operator. Do not assign inaccessible evidence to Reviewer or Tester. If evidence was never recorded, mark that criterion `disposition=undecidable`, name what would settle it, its required source, and why it is unavailable, then plan every remaining criterion in the same round. Escalate only when operator action is required; classify requested evidence as either `retrievable` with its inaccessible store or `never-recorded`.

Follow `AGENTS.md`, `docs/role-capability-matrix.md`, `docs/test-design-discipline.md`, and `docs/dispositive-decision-discipline.md` as canonical authority. Reference their contracts; do not reproduce or replace them.
