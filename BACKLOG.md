# Backlog

Follow-up work items. Each entry is self-contained: act on it without prior conversation context. When an item is finished, remove the entry and note the closing commit in DOGFOOD_LOG.md or the commit message. Check this file before proposing new follow-up work.

## Decision record (durable context, not work items)

Merge policy auto-ff on `acceptance` (2026-06-10). Qwen Code over codex for qwen models - codex 0.137 removed the chat wire API and its harmony/oss path cannot drive qwen (2026-06-10). Thinking must be disabled via `.qwen/settings.json` `generationConfig.reasoning: false`; Ollama ignores `/no_think` and `/v1` `think:false` but honors `reasoning_effort` (2026-06-10). Optional later phase: native tool loop in `AgentTaskRunner`, and `gpt-oss:20b` for codex `--oss`, if qwen-code reliability disappoints on real tasks.

## Integrate goal intake planning into lifecycle execution

Large operator requests should be decomposed into a dependency-aware plan before workers are started. Promote backlog-intake and goal-plan style analysis into the normal lifecycle path so broad objectives produce ordered goal slices, shared context, dependencies, target write scopes, and acceptance criteria without the operator hand-writing separate goals. Done when a lifecycle command can preview the plan, create the planned goals, run only dependency-ready slices, and explain why later slices are held, with tests for single-slice tasks, multi-slice feature work, conflicting slices, and operator edits to the proposed plan.

## Promote deterministic workflow brokers into executable steps

Workers should rely less on prompt reasoning for repeatable repository operations. Turn deterministic broker artifacts for source survey, impact analysis, test selection, verification policy, acceptance evidence, retention, and historical evaluation into invokable workflow steps that the orchestrator can run before or after worker dispatch. Done when workers receive broker handles and concise results instead of prose instructions, broker failures become structured blockers, lifecycle execution can run required brokers automatically, and tests cover broker success, broker failure, stale broker output, and dashboard/CLI visibility.

## Add evidence-based model and provider outcome routing

Model-fit notes, provider failures, retry-after windows, task complexity, cost guards, and worker-result quality should feed a durable routing scorecard. Add a model/provider outcome store that learns from completed tasks and influences future agent selection without overriding hard policy gates. Done when subscription planning can explain why a model/provider was preferred, avoided, or held based on recent outcomes, and tests cover underpowered routes, overkill routes, repeated provider failures, stale evidence decay, and manual override.

Current state: model-fit evidence already exists but only as per-goal `ModelFitSummary` tallies of self-reported `Model fit:` notes (`ModelFitEvidence`), consumed by subscription planning, cost guards, and complexity escalation. Gaps toward this item: (1) evidence is per-goal, not a durable cross-goal store; (2) it counts only self-rated fit notes, not actual task completion/failure outcomes or provider failures; (3) no recency decay or explicit prefer/avoid/held recommendation with explanation; (4) no manual override. Design note (2026-06-13 spark smoke): self-rated fit can diverge from real outcomes - `gpt-5.3-codex-spark` rated itself "adequate" on a dispatch that FAILED (created the file but never committed), so the scorecard must reconcile self-reported fit against recorded dispatch outcomes and weight actual completion/failure higher than the worker's self-assessment. Enabler shipped: the `--subscription-model` CLI flag now pins a per-role subscription model so operators can generate comparative outcome evidence across models.
