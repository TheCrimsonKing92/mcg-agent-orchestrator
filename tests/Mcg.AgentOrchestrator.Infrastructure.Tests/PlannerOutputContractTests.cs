using Mcg.AgentOrchestrator.Infrastructure;

public sealed class PlannerOutputContractTests : WorkerDispatchTestSupport
{
    [Xunit.Fact]
    public void PlannerContract_Live485363d4OrderedIntegrationList_PassesWithoutSequenceKeywords()
    {
        var workingDirectory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(workingDirectory, "seed.txt"), "seed");
        var integrationBody =
            """
            1. Implement parser/options and help.
            2. Implement bounded/history renderer using existing state/classification surfaces.
            3. Wire in-memory and persistent refresh paths.
            4. Add rendering, parser, help, and dual-path command tests.
            5. Add the negative-control receipt.
            6. Run preflight and lifecycle verification without starting a paid worker.
            """;
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Integration seams",
            integrationBody);

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
    }

    [Xunit.Fact]
    public void PlannerContract_NumberedIntegrationPlaceholders_Fail()
    {
        var workingDirectory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(workingDirectory, "seed.txt"), "seed");
        var plan = ReplaceSectionBody(
            PlannerContractPlanFixture(),
            "## Integration seams",
            "1. TBD filler that only pads the section body.\n2. TBD filler that still supplies no substantive integration seam.");

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains("must describe an integration sequence", result.Diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PlannerContract_Live485363d4EmDashNewFileMarkers_Pass()
    {
        var targetBody =
            """
            - `src/Mcg.AgentOrchestrator.App/Cli/CliArgumentParser.RefreshDispatch.cs` — new file: `RefreshDispatchRenderOptions` and a shared extractor that removes/validates history flags while preserving existing targeting and autonomy arguments.
            - `src/Mcg.AgentOrchestrator.App/Cli/ConsoleViews.RefreshDispatch.cs` — new file: `PrintRefreshDispatch`, fixed-line summary mapping, and optional injectable `GoalOperatorDispositionSurface` for hermetic tests.
            - `src/Mcg.AgentOrchestrator.App/Cli/ConsoleViews.TaskDetails.cs` — extend `PrintTaskTimeline` with an optional limit while preserving unlimited behavior for existing callers.
            - `src/Mcg.AgentOrchestrator.App/Cli/CliCommandHandlers.Workers.cs` — parse before `BackgroundDispatchRunner.RefreshLatestProcess`; replace `PrintTask` only in `refresh-dispatch`.
            - `src/Mcg.AgentOrchestrator.App/Cli/CliPersistentStateRunner.cs` — apply the same parser before goal resolution/reconciliation and use the same renderer.
            - `src/Mcg.AgentOrchestrator.App/Cli/CliCommandHelp.cs` — add `RefreshDispatchUsage`, a validated help entry, flag descriptions, and routing.
            - `src/Mcg.AgentOrchestrator.App/Program.cs` — advertise both history options.
            - `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliArgumentNormalizationTests.cs` — parser semantics and malformed-input tests.
            - `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.RefreshDispatchCommands.cs` — new file: deep-history rendering and in-memory command tests.
            - `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.PersistentRunnerCommands.cs` — persistent-path parity, atomicity, and durable-history assertions.
            - `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliHelpTests.cs` — command-specific help and flag-validation coverage.
            Do not change general `PrintTask`, persistence snapshots, `Goal.Timeline`, reconciliation policy, or batch `refresh-dispatches`.
            """;
        var plan = ReplaceSectionBody(PlannerContractPlanFixture(), "## Target seams and symbols", targetBody);

        var result = PlannerOutputContract.Resolve(
            plan,
            string.Empty,
            InfrastructureTestSupport.FindRepositoryRoot());

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
    }

    [Xunit.Fact]
    public void PlannerContract_Live658501ceParenthesizedNewFileMarker_Passes()
    {
        var targetBody =
            """
            Smallest production boundary:

            - `src/Mcg.AgentOrchestrator.Core/Application/ReviewerWorkerResultBlockers.cs`
              - Add a central role-aware predicate such as `TryFindTesterWorkerResultBlocker`.
              - Make `IsAdvisoryNoChangeContractBlocker` depend on that predicate so disabling the single guard restores the old behavior for the negative control.
              - Do not change final-block parsing or role-agnostic `WorkerResultParser` semantics.

            - `src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs`
              - In `DispatchFailureClassifier.Classify`, place the Tester-blocker rule after the existing structured-inconclusive branch but before every success branch.
              - Produce `UnknownFailure`, `OperatorNeeded`, the dedicated rule token, and exact blocker evidence.

            - `src/Mcg.AgentOrchestrator.Core/Reports/TaskOutcomeClassification.cs`
              - Add `TaskOutcomeRules.TesterWorkerResultBlocker` as `RealFailure` and register it in `Produced`.

            - `src/Mcg.AgentOrchestrator.Core/Application/AgentOrchestratorKernel.Recording.cs`
              - In `TryFailWorkerResultBlocker`, fail a Tester blocker independently of `enforceFailureEvidenceRule`, process exit, commit state, or no-change advisory status.
              - Both `RecordTaskVerification` and `RecordDispatchExecutionResult` already converge here.
              - Keep `BuildCompletionMessageWithAdvisoryBlocker` unchanged except for the central predicate preventing Tester blockers from reaching it.

            - `src/Mcg.AgentOrchestrator.Core/Application/AgentOrchestratorKernel.InternalState.cs`
              - Add a Tester blocker branch to `BuildTaskVerificationGate` before `Completed` can pass.
              - Add its retry/operator guidance in `BuildVerificationSuggestedAction`.

            - `src/Mcg.AgentOrchestrator.Core/Reports/VerificationReports.cs`
              - Add typed `VerificationGateReason.TesterWorkerResultBlocker`.

            Focused tests:

            - `tests/Mcg.AgentOrchestrator.Core.Tests/DispatchOutcomeClassifyTests.cs`
            - `tests/Mcg.AgentOrchestrator.Core.Tests/DispatchExecutionTests.cs`
            - `tests/Mcg.AgentOrchestrator.Core.Tests/VerificationAndInputWorklistTests.cs`
            - `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchTestsWorkerResultClassification.cs`
            - Existing registry coverage in `tests/Mcg.AgentOrchestrator.Core.Tests/ModelOutcomeScorecardTests.cs`
            - `docs/negative-controls/658501ce.md` (new file)

            No planned production change to `BackgroundDispatchRunner`, `ConductorDriver`, provider preflight, or `WorkerResultParser`.
            """;
        var plan = ReplaceSectionBody(PlannerContractPlanFixture(), "## Target seams and symbols", targetBody);

        var result = PlannerOutputContract.Resolve(
            plan,
            string.Empty,
            InfrastructureTestSupport.FindRepositoryRoot());

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
    }

    [Xunit.Fact]
    public void PlannerContract_Live485363d4ContextualSiblingCitations_Pass()
    {
        var targetBody =
            """
            - `src\Mcg.AgentOrchestrator.App\Cli\CliArgumentParser.cs`: shared `RefreshDispatch` option parsing and validation.
            - `src\Mcg.AgentOrchestrator.App\Cli\CliCommandHandlers.Workers.cs`: pass parsed options to the in-memory refresh renderer.
            - `src\Mcg.AgentOrchestrator.App\Cli\CliPersistentStateRunner.cs`: pass identical options through persistent refresh and ensure goal/task resolution ignores display flags.
            - `src\Mcg.AgentOrchestrator.App\Cli\ConsoleViews.TaskDetails.cs`: add refresh-specific rendering; leave general `PrintTask` behavior unchanged.
            - Consume existing `DispatchStateSurface.Evaluate`, `DispatchFailureClassifier.Classify`, `WorkerResultBlockers`, and `GoalOperatorDispositionSurface`.
            - `src\Mcg.AgentOrchestrator.App\Cli\CliCommandHelp.cs` and `src\Mcg.AgentOrchestrator.App\Program.cs`: advertise the new syntax.
            - Extend `tests\Mcg.AgentOrchestrator.Infrastructure.Tests\CliCommandTests.PersistentRunnerCommands.cs`, `CliCommandTests.cs`, and `CliHelpTests.cs`.
            """;
        var plan = ReplaceSectionBody(PlannerContractPlanFixture(), "## Target seams and symbols", targetBody);

        var result = PlannerOutputContract.Resolve(
            plan,
            string.Empty,
            InfrastructureTestSupport.FindRepositoryRoot());

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
    }

    [Xunit.Fact]
    public void PlannerContract_ContextualHallucinatedSibling_Fails()
    {
        var targetBody =
            """
            - Extend `tests\Mcg.AgentOrchestrator.Infrastructure.Tests\CliCommandTests.PersistentRunnerCommands.cs`, `HallucinatedSibling.cs`, and `CliHelpTests.cs` with focused contract coverage.
            """;
        var plan = ReplaceSectionBody(PlannerContractPlanFixture(), "## Target seams and symbols", targetBody);

        var result = PlannerOutputContract.Resolve(
            plan,
            string.Empty,
            InfrastructureTestSupport.FindRepositoryRoot());

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains("target citation 'HallucinatedSibling.cs' does not exist", result.Diagnostic, StringComparison.Ordinal);
    }

    private static string ReplaceSectionBody(string plan, string heading, string replacement)
    {
        var normalized = plan.ReplaceLineEndings("\n");
        var bodyStart = normalized.IndexOf(heading, StringComparison.Ordinal) + heading.Length;
        var nextHeading = normalized.IndexOf("\n## ", bodyStart, StringComparison.Ordinal);
        return normalized[..bodyStart] + "\n\n" + replacement.Trim() + "\n" + normalized[nextHeading..];
    }
}
