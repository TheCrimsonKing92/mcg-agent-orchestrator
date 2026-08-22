using System.Globalization;

internal static class SourceSizeRatchet
{
    internal const string DocumentationPath = "docs/god-class-decomposition-plan.md";

    // Seeded at 6e7a90d7b3fe192ae4f1e430cb7452710f88b73c using File.ReadLines(path).Count().
    // When extraction shrinks a guarded file, lower its ceiling in the same change. When growth is
    // unavoidable, raise only that row with an inline justification naming the goal. Never derive these
    // ceilings from the current files or add an opt-out.
    internal static IReadOnlyList<SourceSizeCeiling> SeededCeilings { get; } = Array.AsReadOnly(
        new[]
        {
            // Raised from 8713 for goal 75b85ca1: effective lane selection and structural-coverage
            // threading belong to the gate-plan owner, so extracting them would split that invariant.
            // Raised again for goal 85f0b81d: 227 new behavior lines were extracted to
            // AcceptanceLaneDurationStore, so only call-site lines remained here.
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs", 8779),
            // Goal 682f25a1 re-derived this row after integrating goal c2eae988, whose acceptance
            // cancellation seam had already added 74 net lines before the multi-file ratchet landed.
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.cs", 6156),
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs", 4974),
            // Raised for goal 18afe5f2: missing-build-evidence rejection and commit suppression
            // must run where parsed worker results and authoritative changed paths meet.
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.Infrastructure/Processes/BackgroundDispatchRunner.cs", 3525),
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerProfileDispatcher.cs", 3212),
            new SourceSizeCeiling("src/Mcg.AgentOrchestrator.App/Cli/CliPersistentStateRunner.cs", 4821),
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalAcceptanceVerifierTests.cs", 1566),
            // Goal 682f25a1 re-derived this row after integrating goal c2eae988, whose fault-isolation
            // eligibility coverage had already added 173 net lines before the multi-file ratchet landed.
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTests.cs", 9282),
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.PersistentRunnerCommands.cs", 7038),
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchTestsWorkerResultClassification.cs", 4834),
            // Goal 46ff9f83 adds unequal logical-width/build-permit controls and the typed maximum diagnostic.
            new SourceSizeCeiling("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorBatchLoopTestsParallelAcceptance.cs", 4473),
        });

    internal static IReadOnlyList<SourceSizeViolation> Evaluate(
        string repositoryRoot,
        IEnumerable<SourceSizeCeiling> ceilings,
        Func<string, IEnumerable<string>>? readLines = null)
    {
        readLines ??= File.ReadLines;
        var violations = new List<SourceSizeViolation>();
        foreach (var ceiling in ceilings)
        {
            var sourcePath = Path.Combine(
                repositoryRoot,
                ceiling.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(sourcePath))
            {
                violations.Add(new SourceSizeViolation(
                    ceiling.RelativePath,
                    null,
                    ceiling.MaximumLineCount,
                    BuildMissingFileMessage(ceiling.RelativePath)));
                continue;
            }

            try
            {
                var actualLineCount = readLines(sourcePath).Count();
                if (actualLineCount > ceiling.MaximumLineCount)
                {
                    violations.Add(new SourceSizeViolation(
                        ceiling.RelativePath,
                        actualLineCount,
                        ceiling.MaximumLineCount,
                        BuildOverCeilingMessage(ceiling, actualLineCount)));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                violations.Add(new SourceSizeViolation(
                    ceiling.RelativePath,
                    null,
                    ceiling.MaximumLineCount,
                    BuildUnreadableFileMessage(ceiling.RelativePath, exception)));
            }
        }

        return violations;
    }

    private static string BuildOverCeilingMessage(SourceSizeCeiling ceiling, int actualLineCount)
    {
        return $"{ceiling.RelativePath} has {actualLineCount.ToString(CultureInfo.InvariantCulture)} lines, " +
            $"exceeding the recorded ceiling of {ceiling.MaximumLineCount.ToString(CultureInfo.InvariantCulture)}. " +
            "Extract behavior to a collaborator and lower the ceiling for this entry, or raise the recorded " +
            "ceiling for this entry deliberately with justification in the same change by editing " +
            $"{SeededCeilingsSymbol}. See {DocumentationPath}.";
    }

    private static string BuildMissingFileMessage(string relativePath)
    {
        return $"Guarded file '{relativePath}' does not exist. A rename or delete must update " +
            $"{SeededCeilingsSymbol} in the same change. See {DocumentationPath}.";
    }

    private static string BuildUnreadableFileMessage(string relativePath, Exception exception)
    {
        return $"Guarded file '{relativePath}' could not be read ({exception.GetType().Name}: {exception.Message}). " +
            $"The size ratchet cannot pass without checking every entry. See {DocumentationPath}.";
    }

    private static string SeededCeilingsSymbol =>
        $"{nameof(SourceSizeRatchet)}.{nameof(SeededCeilings)}";
}

internal sealed record SourceSizeCeiling(string RelativePath, int MaximumLineCount);

internal sealed record SourceSizeViolation(
    string RelativePath,
    int? ActualLineCount,
    int MaximumLineCount,
    string Message);
