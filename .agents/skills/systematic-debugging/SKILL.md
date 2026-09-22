---
name: systematic-debugging
description: Debug repeated criterion failures or task-specific acceptance failures through reproduction, causal analysis, one-hypothesis testing, and bounded fixes.
---

# Systematic Debugging

Use these gates in order. Do not edit before gates 1-3 are recorded.

1. **Investigate.** Establish the named failure from an executed run. Record the failing test or check name and quote its assertion or error output.
   - **Run it (default).** Run the smallest command that reproduces the named failure.
   - **Or inspect a retained run.** This satisfies the gate only when you open the actual trusted typed receipt or attachment, such as `prior-goal-evidence.md`, rather than relying on worker narrative or a brief's summary. Confirm every condition: the receipt names the exact test or check and failed assertion being fixed; it reports a non-zero executed count covering that failure; and its source candidate, input, and relevant execution-basis binding match what you are about to edit. Record the receipt identifier, candidate, exact failure, and quoted assertion.
   - **Fall back to running it** if the receipt is absent, unreadable, malformed, selected zero tests, names a different test, assertion, input, or candidate, is narrative-only, or is contradicted by other evidence. Any one rejection is enough.
   - **Match discriminating conditions for environment-sensitive failures.** Source identity alone cannot admit timing, locking, ordering, path, tool-version, platform, or similar failures. The receipt must also record the conditions that distinguish pass from fail; otherwise run the reproduction.
   - If reproduction is unavailable, report the command and output and classify the cause as undetermined or plumbing.

   Inspecting a matching retained run reuses a reproduction; it does not waive one. See [worker guidance discipline](../../../docs/worker-guidance-discipline.md#failure-evidence-a-worker-already-has) for the brief-side contract.
2. **Analyze patterns.** Trace the failing path, compare nearby working paths, and identify what changed. Follow the repository diagnosis rules; do not infer race or contention without a controlled serialized-versus-concurrent comparison that isolates or instruments the claimed resource.
3. **State and test one hypothesis.** Name the failure class and write one falsifiable root-cause hypothesis. Change one diagnostic variable or run one minimal test, then record whether the result supports or rejects it. For a deterministic message-contract failure, the matching failed assertion plus inspection of the changed emitting source can be the minimal hypothesis test; another process invocation is not a ritual independent of evidence.
4. **Implement.** Make the smallest root-cause fix. In the current session, rerun the original reproduction and the focused regression check, inspect their output, and record a non-zero executed count. A retained pre-edit receipt can never satisfy this post-fix gate. After three failed fixes, stop mutating and escalate with the three hypotheses, changes, and results plus the architectural or specification question that must be settled.

Independently condensed from the `systematic-debugging` ideas in [obra/superpowers](https://github.com/obra/superpowers/blob/main/skills/systematic-debugging/SKILL.md), MIT licensed, copyright 2025 Jesse Vincent.
