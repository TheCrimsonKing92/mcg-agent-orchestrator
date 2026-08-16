# Operator execution: the four failing persistent-runner tests now pass

Executed by the operator lane, which can launch test hosts. Subscription workers cannot, so this
receipt is the executed evidence for the acceptance criteria the Tester deferred.

## Result

Candidate `34405b06f01e`, disposable detached checkout, no parallelism:

    scripts/Invoke-TestSummary.ps1
      -Target tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj
      -Filter "FullyQualifiedName~CliCommandTestsPersistentRunnerCommands"

    total=226 passed=226 failed=0 skipped=0

All four tests that failed acceptance at `3c54a4bad773` now pass:

    CliPersistentStateRunner_goal_create_delivery_failure_is_durable_and_retry_only
    CliPersistentStateRunner_goal_create_flushes_deferred_side_effects_after_commit
    CliPersistentStateRunner_concurrent_goal_creates_have_exactly_one_backlog_winner
    GoalIntake_dispatch_replay_stops_before_second_preflight

## What was actually wrong, for the record

The four failures were one behaviour, not four bugs. `PersistentRunnerGoalCreateFlushesDeferredSideEffectsAfterCommit`
alone carried three of the four distinct assertion types, and all three assert that goal creation
must **defer** refinement:

    Assert.Null(created.RefinedSpec);
    evt.Message.StartsWith("spec_refinement outcome=pending")   // Assert.StartsWith
    Assert.Empty(CollaborationItemStore ... ListAsync(...))      // Assert.Empty
    Assert.False(File.Exists(GoalLifecycleEventsDirectory ...))  // Assert.False

`"spec_refinement outcome=pending"` is emitted only by `GoalRefinementWorkCoordinator`. The
competing message comes from `GoalRefinementGate`, which emits
`spec_refinement outcome={disposition}`. The `StartsWith` failure therefore meant refinement had
actually run, and running it is also what created the clarification items and wrote the lifecycle
events file — hence the other two assertions.

The mechanism was this call at the top of `CliPersistentStateRunner.ExecuteCommand`, removed in
`34405b06`:

    if (stateRepository is IOrchestratorStateOutboxRepository refinementOutboxRepository)
        GoalRefinementWorkCoordinator.TryLaunchFirstPending(refinementOutboxRepository, workspace);

It ran on **every** CLI command, so `RecordPending` during goal creation was immediately followed
by an eager launch in the same process. Recording pending work and then synchronously draining it
is not deferral.

An operator note drafted before this commit attributed the inline execution to the create path
calling `GoalRefinementGate` directly. That was wrong and was not delivered. The gate is invoked
only from the spec-consumer paths (`CliCommandHandlers.Workers.cs`,
`GoalManagementCommandService.Advancement.cs`), which is correct and should stay.

## Note on the call-site count

`GoalRefinementWorkCoordinator.RecordPending` now has four call sites:

    CliPersistentStateRunner.cs:1967
    CliPersistentStateRunner.cs:3358
    DashboardEndpoints.Goals.cs:27
    GoalLifecycleCommands.cs:158

The per-call-site convention was chosen over a single choke point two rounds ago, when there were
two. It has since grown by one per round. This is not a blocker for the current change, but a
follow-up should consolidate: each new creation path is another place the pending record can be
omitted, and the previous round's defect was exactly an omission of that kind.

## Environment note

Test hosts in this repository could die before discovery with
`Win32Exception (5): Access is denied` from `AssemblyTempRedirect.IsProcessAlive` inside a
`ModuleInitializer`, reporting `tests_executed=0` with no failing test. Fixed on `main` in
`0b84061a`; filed as backlog `6152e090`. The run above was performed with that fix present.
