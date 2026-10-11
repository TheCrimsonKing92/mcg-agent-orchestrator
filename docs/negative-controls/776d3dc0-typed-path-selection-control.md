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

Conductor receipt: `finding-evidence-ef69b884ae123f44d11a27c1`, candidate
`13505758f8dba6747846276da4cb191666439531`. The request selected
`TaskBriefPathListEmissionTests`, `WorkerContextRendererDispatchPathTests` and
`WorkerContextRendererReviewerScopeSelectionTests`, with `negative_control=revert-src` on the
renderer path above. Receipt outcome: `negative-control-demonstrated`.

The source-reverted arm executed 32 tests: 17 passed, 15 failed; managed-runner exit code 2.
Core passed 7/7; Infrastructure passed 10/25. This was an assertion failure, not a compile failure.
The heading control failed at
`WorkerContextRendererReviewerScopeSelectionTests.cs:198`:

```text
Assert.Equal() Failure: Strings differ
Expected: ···" base: baseline\r\nChanged files: 125; complete path"···
Actual:   ···" base: baseline\r\nChanged entries: fixture wording;"···
```

Retained TRX directory (shared with GREEN):
`.orchestrator\pre-review-evidence-attempts\776d3dc0ae09442a8b4828ef17bf6fe4` (relative to the repository root).
RED TRX filenames in that directory:

```text
776d3dc0-pre-review-20261011022537842-f010154e287a4ca883afa3f9a9783715-sourcereverted.reviewer-focused-evidence-core-tests-fullyqualifiedname-taskbriefpathlistemissiontests.trx
776d3dc0-pre-review-20261011022537842-f010154e287a4ca883afa3f9a9783715-sourcereverted.reviewer-focused-evidence-infrastructure-tests-fullyqualifiedname-workercontextrendererdispat-4c86e03106e5ac65.trx
```

Their SHA-256 values match the receipt: Core
`f365ae7929f04f7d79f078b58e717fc275bf761a73f9af9faee159920290d84a`; Infrastructure
`793f5a6e98bcddee2180cedf22a5eff7ca9c6c5f80d294f29aefc2557f26c17d`.

## GREEN receipt

The candidate arm of the same receipt passed 32/32 tests with managed-runner exit code 0:
`TaskBriefPathListEmissionTests` 7/7, `WorkerContextRendererDispatchPathTests` 10/10 and
`WorkerContextRendererReviewerScopeSelectionTests` 15/15. GREEN TRX filenames in the retained
directory above:

```text
776d3dc0-pre-review-20261011022537842-f010154e287a4ca883afa3f9a9783715-candidate.reviewer-focused-evidence-core-tests-fullyqualifiedname-taskbriefpathlistemissiontests.trx
776d3dc0-pre-review-20261011022537842-f010154e287a4ca883afa3f9a9783715-candidate.reviewer-focused-evidence-infrastructure-tests-fullyqualifiedname-workercontextrendererdispatchpat-e46ac9647da5dcf5.trx
```

Their SHA-256 values match the receipt: Core
`80c92a3180f46c69c50f3469ef77ab7f00d61e7100597ea0843360aab2712047`; Infrastructure
`001d8923d5c6159556e78229d85ca3002e03912b19495abd0be7e3f849b49dee`.
The receipt is packaged as
`goal/review-finding-receipts/b3eba4c1d08fcaea64843bcbd03338a98e3da7eaad98324673ea7b8b2f6267b5.json`.
The Developer inspected all four hash-matching TRX files when recording these receipts; no tests
were rerun for this documentation repair.

## Stored-format stop-condition check

`TaskBriefPathList` and its line coordinates are per-dispatch in-memory data. Legacy boundary
emission, artifact formats, role visibility and artifact materialization are unchanged. The
capped-scope role theory compares the complete Reviewer artifact hashes against the pre-change
typed-output baseline and independently serialized expected bytes. The baseline removes projected
segments before passing the remaining text to the legacy preview policy, matching comparison
base `7bda161742796d9b02bc5ca435e3c74e856e1ea1` rather than legacy marker-removal separators.
The separator theory also pins the pre-change current-brief bytes for all three roles, with and
without removal flags. Developer and Tester artifact identity/hash maps equal that baseline;
Tester convergence paths remain complete. Literal-bearing user text is preserved by the typed
path, so Reviewer `brief/current.md` and its derived manifest intentionally have different hashes
from the pre-change text policy on that adversarial fixture. The receipts above remain evidence
for their recorded candidate; execution of the revised equivalence tests is deferred to Tester.
