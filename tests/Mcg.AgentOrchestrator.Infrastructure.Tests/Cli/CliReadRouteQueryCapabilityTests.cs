using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its kernel, repository and temporary workspace.
public sealed class CliReadRouteQueryCapabilityTests : CliTaskQueryTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData("readiness", "abc10000")]
    [Xunit.InlineData("readiness")]
    [Xunit.InlineData("goals")]
    [Xunit.InlineData("goals", "--json")]
    [Xunit.InlineData("model-outcomes")]
    public void QueryOnlyView_MatchesDirectOutputWithoutWriterEffects(params string[] args)
    {
        var root = CreateTempDirectory();
        try
        {
            var probe = new ProbeStateRepository(CliInspectionReadOnlyRouteTests.CreateInspectionSeed())
                { ThrowOnOutbox = true };
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var viewOutput = ExecuteAndCapture(new QueryOnlyStateView(probe));
            var directOutput = ExecuteAndCapture(probe);

            Xunit.Assert.NotEmpty(viewOutput);
            Xunit.Assert.Equal(NormalizeOutput(directOutput, args), NormalizeOutput(viewOutput, args));
            Xunit.Assert.Equal(0, probe.SaveAttempts);
            Xunit.Assert.Equal(0, probe.MergeSaveAttempts);
            Xunit.Assert.Equal(0, probe.MutationAttempts);
            Xunit.Assert.Equal(0, probe.OutboxClaimAttempts);

            string ExecuteAndCapture(IOrchestratorStateQueries queries)
            {
                IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
                var profiles = WorkerProfileCatalog.Default();
                Goal? currentGoal = null;
                return CaptureConsole(() =>
                {
                    Xunit.Assert.True(CliReadOnlyCommandRunner.TryExecute(
                        args, queries, workspace, new InMemoryModelProviderRegistry([]), null,
                        ref agents, ref profiles, ref currentGoal, out var changed));
                    Xunit.Assert.False(changed);
                });
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void Normalizer_PreservesFormattingAndNonTimestampFields()
    {
        const string output = "{\r\n  \"generatedAt\" : \"2026-10-25T12:34:56.1234567Z\",\r\n"
            + "  \"payload\": { \"objective\": \"2026-10-25T12:34:56.1234567Z\", \"age\": 42 }\r\n}\r\n";
        const string expected = "{\r\n  \"generatedAt\" : \"2026-10-25T00:00:00.0000000Z\",\r\n"
            + "  \"payload\": { \"objective\": \"2026-10-25T12:34:56.1234567Z\", \"age\": 42 }\r\n}\r\n";

        Xunit.Assert.Equal(expected, NormalizeOutput(output, ["goals", "--json"]));
        Xunit.Assert.Equal(output, NormalizeOutput(output, ["goals"]));
    }

    private static string NormalizeOutput(string output, IReadOnlyList<string> args)
    {
        if (!args.SequenceEqual(new[] { "goals", "--json" }))
            return output;

        // Explicit exception list: goals --json's top-level generatedAt (UTC ISO-8601).
        // This envelope has no ages derived from now. Preserve all other bytes, including whitespace.
        using var envelope = JsonDocument.Parse(output);
        var match = Xunit.Assert.Single(Regex.Matches(output,
            "\"generatedAt\"\\s*:\\s*\"(?<timestamp>[^\"]+)\""));
        var span = match.Groups["timestamp"];
        Xunit.Assert.Equal(envelope.RootElement.GetProperty("generatedAt").GetString(), span.Value);
        var timestamp = DateTime.ParseExact(span.Value, "O", CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        Xunit.Assert.Equal(DateTimeKind.Utc, timestamp.Kind);

        const string replacement = "2026-10-25T00:00:00.0000000Z";
        var normalized = output[..span.Index] + replacement + output[(span.Index + span.Length)..];
        // Restoring only the listed timestamp span must recover the complete original output.
        Xunit.Assert.Equal(output,
            normalized[..span.Index] + span.Value + normalized[(span.Index + replacement.Length)..]);
        return normalized;
    }

    [Xunit.Fact]
    public void QueryOnlyView_LoadsSameConductKernelGoalsAndDependencyStatuses()
    {
        var root = CreateTempDirectory();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var dependency = kernel.CreateGoal("Completed dependency",
                [new TaskSpec(TaskId.New(), "Done", AgentRole.Developer)]);
            var dependent = kernel.CreateGoal("Active dependent",
                [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            var parked = kernel.CreateGoal("Parked goal");
            kernel.ActivateGoal(dependency.Id, AgentCatalog.Default().Agents);
            kernel.ActivateGoal(dependent.Id, AgentCatalog.Default().Agents);
            kernel.SetGoalDependency(dependent.Id, dependency.Id);
            kernel.ParkGoal(parked.Id, "Operator wait");
            var snapshot = kernel.ExportSnapshot();
            kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
            {
                Goals = snapshot.Goals.Select(goal => goal.Id == dependency.Id.Value
                    ? goal with { Status = GoalStatus.Completed }
                    : goal).ToArray()
            });
            var probe = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };

            var viewKernel = CliPersistentStateRunner.LoadConductLoopKernel(
                new QueryOnlyStateView(probe), executionDirectory: root);
            var directKernel = CliPersistentStateRunner.LoadConductLoopKernel(probe, executionDirectory: root);

            Xunit.Assert.Equal(dependent.Id, Xunit.Assert.Single(viewKernel.Goals).Id);
            Xunit.Assert.Equal(
                directKernel.Goals.Select(goal => goal.Id.Value).Order(StringComparer.Ordinal).ToArray(),
                viewKernel.Goals.Select(goal => goal.Id.Value).Order(StringComparer.Ordinal).ToArray());
            Xunit.Assert.Contains(dependency.Id, viewKernel.Goals.Single().DependsOn);
            Xunit.Assert.Equal(GoalStatus.Completed.ToString(), viewKernel.KnownDependencyGoalStatuses[dependency.Id]);
            Xunit.Assert.Equal(GoalStatus.Parked.ToString(), viewKernel.KnownDependencyGoalStatuses[parked.Id]);
            Xunit.Assert.Equal(
                directKernel.KnownDependencyGoalStatuses.OrderBy(pair => pair.Key.Value, StringComparer.Ordinal).ToArray(),
                viewKernel.KnownDependencyGoalStatuses.OrderBy(pair => pair.Key.Value, StringComparer.Ordinal).ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class QueryOnlyStateView(ProbeStateRepository probe) : IOrchestratorStateQueries
    {
        private readonly IOrchestratorStateQueries inner = probe;

        public Task<AgentOrchestratorKernel> LoadBoardSnapshotAsync(CancellationToken cancellationToken = default) =>
            inner.LoadBoardSnapshotAsync(cancellationToken);

        public Task<AgentOrchestratorKernel> LoadGoalsAsync(
            IReadOnlyCollection<GoalId> goalIds, CancellationToken cancellationToken = default) =>
            inner.LoadGoalsAsync(goalIds, cancellationToken);

        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default) =>
            inner.ListGoalMetadataAsync(cancellationToken);

        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(
            bool includeTerminalCreatedAt, CancellationToken cancellationToken = default) =>
            inner.ListGoalMetadataAsync(includeTerminalCreatedAt, cancellationToken);

        public Task<IReadOnlyList<HumanInputRequestSnapshot>> ListOpenHumanInputRequestsAsync(
            CancellationToken cancellationToken = default) =>
            inner.ListOpenHumanInputRequestsAsync(cancellationToken);

        public Task<IReadOnlyList<GoalId>> ListGateTestObligationGoalIdsAsync(
            IReadOnlyCollection<GoalId>? goalIds = null, CancellationToken cancellationToken = default) =>
            inner.ListGateTestObligationGoalIdsAsync(goalIds, cancellationToken);

        public Task<IReadOnlyList<TerminalOwnerQuestionHold>> ListTerminalOwnerQuestionHoldsAsync(
            IReadOnlyCollection<GoalId> goalIds, CancellationToken cancellationToken = default) =>
            inner.ListTerminalOwnerQuestionHoldsAsync(goalIds, cancellationToken);

        public Task<IReadOnlyList<GoalSummary>> ListGoalIdStatusesAsync(CancellationToken cancellationToken = default) =>
            inner.ListGoalIdStatusesAsync(cancellationToken);

        public Task<IReadOnlyList<GoalSummary>> ListConductLoopGoalMetadataAsync(
            CancellationToken cancellationToken = default) =>
            inner.ListConductLoopGoalMetadataAsync(cancellationToken);

        public Task<IReadOnlyList<ModelOutcomeRecord>> BuildModelOutcomeScorecardAsync(
            int windowSize = ModelOutcomeScorecard.DefaultWindowSize, CancellationToken cancellationToken = default) =>
            inner.BuildModelOutcomeScorecardAsync(windowSize, cancellationToken);
    }
}
