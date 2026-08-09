# Prior-Evidence Packaging Rollout Measurement

The outcome metric is **rounds-to-first-valid-plan** for goals whose author-controlled briefs cite prior
goal or task evidence. A valid plan satisfies the deterministic Planner output contract, including an
evidence-backed `premise-invalid` result. A missing-evidence blocker is not a valid plan. Also report
missing-evidence escalations per eligible goal; do not substitute package completeness for either outcome.

Eligible packages emit `prior_evidence_package=v1` with cited and packaged entity counts in
`prior-goal-evidence.md`. The cohort and round outcomes live in the task-package artifacts,
`state.db` goal/task verification history and timeline, and the corresponding
`.orchestrator/goal-events/<goal>.jsonl` events. A post-rollout dogfood receipt should record raw per-goal
values and the median, disclosing the sample size when fewer than five eligible goals exist.

## Supplied pre-change baseline

| Goal | Missing-evidence escalations | Rounds to first valid plan |
| --- | ---: | --- |
| `338d5bda` | 4 Planner escalations for receipts | Not recoverable from the supplied evidence |
| `6d993e03` | At least 1; the goal blocked for the same missing-evidence reason | Not reached in the supplied evidence |

No post-rollout sample existed at implementation time. It must be measured from subsequent eligible
dogfood goals rather than fabricated by unit tests.
