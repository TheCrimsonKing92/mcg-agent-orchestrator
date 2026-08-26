using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceGatePhaseTimingTests : GoalAcceptanceVerifierTestBase
{
    private const string Manifest = """
        {
          "version": 1,
          "checks": [
            { "name": "phase seam", "type": "command", "command": "git", "arguments": ["diff", "--check"] }
          ],
          "forbiddenChangedPathGlobs": []
        }
        """;

    [Fact]
    public async Task CompletedGateEmitsNamedReconciledPhaseBreakdown()
    {
        var root = CreateManifestWorkspace(Manifest);
        var time = new RecordingTimeProvider();
        var progress = new List<AcceptanceGateProgress>();
        try
        {
            var verifier = new GoalAcceptanceVerifier((_, _, _) =>
            {
                time.Advance(TimeSpan.FromSeconds(1));
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, string.Empty));
            }, time);
            using var sink = GoalAcceptanceVerifier.PushGateProgressSink(progress.Add);

            var result = await verifier.RunAsync(
                root,
                new GoalId("12345678123456781234567812345678"));

            Assert.True(result.Passed);
            var emitted = Assert.Single(progress, item => item.Phase == "gate-phase-breakdown");
            var breakdown = Assert.IsType<AcceptanceGatePhaseBreakdown>(emitted.PhaseBreakdown);
            Assert.Equal("verifier-run", breakdown.Scope);
            Assert.Equal("completed", breakdown.Outcome);
            Assert.Equal(breakdown.TotalDuration, emitted.Elapsed);
            Assert.False(emitted.CurrentTarget.Contains(' '));
            Assert.Contains("scope=verifier-run;outcome=completed", emitted.CurrentTarget, StringComparison.Ordinal);
            Assert.Equal(
                breakdown.TotalDuration,
                breakdown.AttributedPhaseDuration +
                breakdown.LaneExecutionDuration +
                breakdown.UnattributedDuration);
            var phases = breakdown.Phases.ToDictionary(phase => phase.Name, StringComparer.Ordinal);
            foreach (var phaseName in new[]
            {
                AcceptanceGatePhaseNames.GatePlan,
                AcceptanceGatePhaseNames.BuildServerShutdown,
                AcceptanceGatePhaseNames.PlanConstruction,
                AcceptanceGatePhaseNames.CheckExecution,
                AcceptanceGatePhaseNames.PolicySynthesis,
                AcceptanceGatePhaseNames.StructuralCoverage,
                AcceptanceGatePhaseNames.AdvisoryAndTamper,
                AcceptanceGatePhaseNames.Finalize
            })
            {
                Assert.True(phases.ContainsKey(phaseName), $"Missing phase '{phaseName}'.");
                Assert.True(phases[phaseName].Duration >= TimeSpan.Zero);
            }
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Fact]
    public async Task FailedGateStillEmitsCompletedPhases()
    {
        var root = CreateManifestWorkspace(Manifest);
        var time = new RecordingTimeProvider();
        var progress = new List<AcceptanceGateProgress>();
        var call = 0;
        try
        {
            var verifier = new GoalAcceptanceVerifier((_, _, _) =>
            {
                time.Advance(TimeSpan.FromSeconds(1));
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                    call++ == 0 ? 0 : 7,
                    "deterministic failure"));
            }, time);
            using var sink = GoalAcceptanceVerifier.PushGateProgressSink(progress.Add);

            var result = await verifier.RunAsync(root);

            Assert.False(result.Passed);
            var breakdown = Assert.IsType<AcceptanceGatePhaseBreakdown>(
                Assert.Single(progress, item => item.Phase == "gate-phase-breakdown").PhaseBreakdown);
            Assert.Equal("failed", breakdown.Outcome);
            Assert.Contains(breakdown.Phases, phase => phase.Name == AcceptanceGatePhaseNames.Finalize);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Fact]
    public async Task FaultedGateStillEmitsCompletedPhasesWithoutMaskingFault()
    {
        var root = CreateManifestWorkspace(Manifest);
        var progress = new List<AcceptanceGateProgress>();
        try
        {
            var verifier = new GoalAcceptanceVerifier((_, _, _) =>
                Task.FromException<GoalAcceptanceVerifier.CommandResult>(
                    new InvalidOperationException("deterministic runner fault")));
            using var sink = GoalAcceptanceVerifier.PushGateProgressSink(item =>
            {
                progress.Add(item);
                throw new InvalidOperationException("observer fault must be swallowed");
            });

            var exception = await Assert.ThrowsAsync<AcceptanceGateEngineException>(() => verifier.RunAsync(root));

            var innerException = Assert.IsType<InvalidOperationException>(exception.InnerException);
            Assert.Equal("deterministic runner fault", innerException.Message);
            Assert.Equal("build-server-shutdown", exception.GatePhase);
            Assert.Equal("dotnet build-server shutdown", exception.GateTarget);
            Assert.Contains("deterministic runner fault", exception.FaultStack, StringComparison.Ordinal);
            var breakdown = Assert.IsType<AcceptanceGatePhaseBreakdown>(
                Assert.Single(progress, item => item.Phase == "gate-phase-breakdown").PhaseBreakdown);
            Assert.Equal("faulted", breakdown.Outcome);
            Assert.Contains(breakdown.Phases, phase => phase.Name == AcceptanceGatePhaseNames.BuildServerShutdown);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Fact]
    public async Task CancelledGateStillEmitsCompletedPhasesWithoutMaskingCancellation()
    {
        var root = CreateManifestWorkspace(Manifest);
        var progress = new List<AcceptanceGateProgress>();
        using var cancellation = new CancellationTokenSource();
        try
        {
            var verifier = new GoalAcceptanceVerifier((_, _, token) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<GoalAcceptanceVerifier.CommandResult>(token);
            });
            using var sink = GoalAcceptanceVerifier.PushGateProgressSink(progress.Add);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                verifier.RunAsync(root, cancellationToken: cancellation.Token));

            var breakdown = Assert.IsType<AcceptanceGatePhaseBreakdown>(
                Assert.Single(progress, item => item.Phase == "gate-phase-breakdown").PhaseBreakdown);
            Assert.Equal("cancelled", breakdown.Outcome);
            Assert.Contains(breakdown.Phases, phase => phase.Name == AcceptanceGatePhaseNames.GatePlan);
            Assert.Contains(breakdown.Phases, phase => phase.Name == AcceptanceGatePhaseNames.BuildServerShutdown);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }
}
