# Negative control: stale verifying findings

The subscription Developer may run only the sanctioned worker build, so the acceptance lane owns the
executable RED/GREEN receipts for these tests.

- With the currency predicate removed from the convergence-brief history scan,
  `StaleTesterFindingAfterOperatorCloseDoesNotReopenDeveloper` and
  `StaleTesterFindingAfterInvalidationDispatchesReviewerNotDeveloper` must fail at `Assert.Empty` because
  the two stale Tester findings remain visible.
- With latest-completed-result supersession removed,
  `ManualPassSupersedesTesterFindingWithoutNewDeveloperCompletion` must fail at `Assert.Empty` because the
  operator's successful closure no longer resolves the earlier finding state.
- With every later `WorkerResultPresent == false` record treated as a resolution,
  `ApparatusFailedTesterRerunDoesNotResolveItsCurrentFinding` must fail because the expected two current
  Tester findings collapse to an empty result.
- At the pre-fix routing behavior, each stale-finding fixture must also fail its retry-target assertion by
  observing the Developer task instead of the Reviewer task.

Acceptance must record those focused RED failures with only the named behavior disabled, restore the
production behavior, and then record focused GREEN results. A successful worker build is not a substitute.
