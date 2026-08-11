---
name: criterion-ownership-planning
description: Plan every acceptance criterion with a feasible evidence owner, owning seam, verification class, required evidence, and stop condition.
---

# Criterion Ownership Planning

Use this procedure for Planner tasks.

For every criterion, record:

- evidence owner, after checking role feasibility against `docs/role-capability-matrix.md`;
- owning code seam or external contract;
- verification class: `TEST-VERIFIABLE` or `REAL-WORLD-DEPENDENT`;
- required evidence; and
- stop or escalation condition.

Route full-suite, test-host, and acceptance evidence that workers cannot produce to Acceptance. Route live-system and post-landing evidence to the operator. Do not assign inaccessible evidence to Reviewer or Tester. For missing evidence, follow the centrally emitted Planner disposition and evidence-request contract in `src/Mcg.AgentOrchestrator.Core/AgentOutputDirectives.cs` and `src/Mcg.AgentOrchestrator.Core/Application/AgentOrchestratorKernel.TaskBriefs.cs`; do not reproduce its output schema here. Name what would settle the gap, its required source, and why it is unavailable, then plan every remaining criterion in the same round. Escalate only when operator action is required.

Follow `AGENTS.md`, `docs/role-capability-matrix.md`, `docs/test-design-discipline.md`, and `docs/dispositive-decision-discipline.md` as canonical authority. Reference their contracts; do not reproduce or replace them.
