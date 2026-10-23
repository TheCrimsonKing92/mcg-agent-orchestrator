# Completion transport negative controls

Developer verification is build-only. No behavioral RED/GREEN receipt is claimed here.
Acceptance must execute the new `SubscriptionCompletionTransportTests` and the existing
`GoalRefinementTests`, `WorkerContextUsageReceiptTests`, `ClaudeSubscriptionEffortArgumentTests`,
and `ProgressiveReviewGlanceTests` guards, with nonzero execution counts.

In the candidate worktree, temporarily replace the body of
`CompleteSemanticTextWithResultAsync` after its `InvokeAsync` call with:

```csharp
return new(result.StandardOutput, result.RawStandardOutput, result.ExitCode, null);
```

Build and execute the focused class in that same worktree. Expected RED:
`FinalCompletedAssistantWins` and `FinalTurnExcludesEarlierAnswers` compare domain text
against raw JSONL; `RefinementAcceptsNormalizedSpec` sees Fallback instead of Completed;
`InvalidTransportFailsClosed` sees a successful adapter result for exit-zero malformed
transport; `NonzeroExitPreservesFailure` loses the explicit provider-exit classification.
Restore the method and rebuild before recording GREEN for the same selections. Retain
the actual assertion messages, commands, candidate identity, and executed counts.

To isolate raw retention, temporarily change `RunSubscriptionRefinerAsync` to persist
`completion.Text` instead of `completion.RawStandardOutput`. Expected RED:
`RefinementAcceptsNormalizedSpec` fails the exact persisted-file equality. Restore and
rebuild before recording GREEN. The injected runner models the production trim boundary
in `InvocationKeepsInputAndCleanup`; no live provider call belongs in these controls.

The Operator still owns one new normal codex-cli/gpt-6.1-sol refinement after landing
and activation, with provider-attributed success, retained event stream, and inspected
domain criteria. Independent Claude-family implementation review remains the Reviewer’s
obligation; the supplied Opus receipt reviewed the specification only.
