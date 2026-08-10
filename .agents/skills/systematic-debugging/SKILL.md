---
name: systematic-debugging
description: Debug repeated criterion failures or task-specific acceptance failures through reproduction, causal analysis, one-hypothesis testing, and bounded fixes.
---

# Systematic Debugging

Use these gates in order. Do not edit before gates 1-3 are recorded.

1. **Investigate.** Run the smallest command that reproduces the named failure. Record the failing test or check name and quote its assertion or error output. If reproduction is unavailable, report the command and output and classify the cause as undetermined or plumbing.
2. **Analyze patterns.** Trace the failing path, compare nearby working paths, and identify what changed. Follow the repository diagnosis rules; do not infer race or contention without a controlled serialized-versus-concurrent comparison that isolates or instruments the claimed resource.
3. **State and test one hypothesis.** Name the failure class and write one falsifiable root-cause hypothesis. Change one diagnostic variable or run one minimal test, then record whether the result supports or rejects it.
4. **Implement.** Make the smallest root-cause fix, rerun the original reproduction, and then run the focused regression check. After three failed fixes, stop mutating and escalate with the three hypotheses, changes, and results plus the architectural or specification question that must be settled.

Independently condensed from the `systematic-debugging` ideas in [obra/superpowers](https://github.com/obra/superpowers/blob/main/skills/systematic-debugging/SKILL.md), MIT licensed, copyright 2025 Jesse Vincent.
