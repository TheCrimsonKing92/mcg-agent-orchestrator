# Negative control: typed reviewer path selection

Goal: `776d3dc0`
Control: `WorkerContextRendererReviewerScopeSelectionTests.ProducerHeadingMutationCannotChangeTypedPathSelection`

## Mutation

The Tester requests `negative_control=revert-src` for
`src/Mcg.AgentOrchestrator.Execution/Workers/WorkerContextRenderer.cs` only, restoring the
pre-change renderer from the goal base. No test, typed producer metadata, or legacy residual
changes for this mutation. The reverted renderer calls `ApplyReviewerPreviewPolicy` on the
rendered current brief instead of selecting segment line ranges from typed data.

The fixture renames the producer heading and both count-prefix lines while retaining typed
coordinates. With the mutation, the changed and conflict path lines remain visible and the
count-only lines are absent. The unchanged legacy ingress is also exercised: its renamed scope
remains intact, while it still strips the convergence paths under their unchanged marker.

## RED receipt

Deferred to the Tester/Conductor evidence request selecting
`WorkerContextRendererReviewerScopeSelectionTests`, with `negative_control=revert-src` on the
renderer path above. Required result: `negative-control-demonstrated`, with the failing assertion
in `ProducerHeadingMutationCannotChangeTypedPathSelection` showing the unexpected path lines or
missing count-only lines. A compile failure does not satisfy this control.

The Developer is prohibited from executing tests. No RED execution receipt exists in this round;
the test-capable lane must retain its run identity, counts, exit code and failing assertion here.

## GREEN receipt

Deferred to the same test-capable lane after restoring the candidate renderer. Run
`WorkerContextRendererReviewerScopeSelectionTests` and `TaskBriefPathListEmissionTests`, along
with the existing `WorkerContextRendererDispatchPathTests` identity-hash control. Retain the
passing run identity, counts and exit code here. A worker build is compile evidence only.

## Stored-format stop-condition check

`TaskBriefPathList` and its line coordinates are per-dispatch in-memory data. Legacy boundary
emission, artifact formats, role visibility and artifact materialization are unchanged. The
capped-scope role theory compares the complete Reviewer artifact hashes against the legacy
package and independently serialized expected bytes. Developer and Tester artifact identity/hash
maps are unchanged; Tester convergence paths remain complete. Literal-bearing user text is
preserved by the typed path, so Reviewer `brief/current.md` and its derived manifest intentionally
have different hashes from the legacy path on that adversarial fixture.
