using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    internal IMainSuspectReleaseProbe CreateMainSuspectReleaseProbe() => new MainSuspectReleaseProbe(this);

    private sealed class MainSuspectReleaseProbe(ConductorDriver driver) : IMainSuspectReleaseProbe
    {
        public Task<MainSuspectReleaseProbeResult> RunAsync(string tip, IReadOnlyList<string> tests,
            CancellationToken cancellationToken) => Task.Run(() => Run(tip, tests, cancellationToken), cancellationToken);

        private MainSuspectReleaseProbeResult Run(string tip, IReadOnlyList<string> tests, CancellationToken cancellationToken)
        {
            var receipt = $"main-suspect-release:{tip}:{Guid.NewGuid():N}";
            var workspace = driver._cohortWorkspace;
            var verifier = driver._cohortAcceptanceVerifier;
            if (workspace is null || verifier is null)
                return new(MainSuspectReleaseProbeOutcome.CouldNotRun, receipt, "runner-unavailable");
            var selection = ConductorAcceptanceCohortFocusedAttribution.TrySelect(tests.ToArray(), []);
            if (selection is null)
                return new(MainSuspectReleaseProbeOutcome.CouldNotRun, receipt, "selection-unavailable");

            var root = workspace.ExecutionDirectory;
            var checkout = Path.Combine(OrchestratorTempRoot.GetPurposeDirectory("main-suspect-release-worktrees"),
                tip[..Math.Min(12, tip.Length)], Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.GetDirectoryName(checkout)!);
            cancellationToken.ThrowIfCancellationRequested();
            var added = GitCli.Run(root, "worktree", "add", "--detach", "--quiet", checkout, tip);
            if (!added.Succeeded || added.DrainTimedOut)
            {
                // A drain fault can occur after git already registered the checkout.
                if (Directory.Exists(checkout)) Remove(root, checkout);
                return new(MainSuspectReleaseProbeOutcome.CouldNotRun, receipt, $"checkout-unavailable:exit={added.ExitCode}");
            }
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var head = GoalWorktrees.ResolveRequiredRef(checkout, "HEAD");
                if (!string.Equals(head, tip, StringComparison.OrdinalIgnoreCase))
                    return new(MainSuspectReleaseProbeOutcome.CouldNotRun, receipt, "checkout-revision-mismatch");
                using var lease = driver.CohortPartitionStableSlotLeaseSource is { } source
                    ? source($"main-suspect-release:{tip}", cancellationToken)
                    : driver._parallelAcceptanceAttemptCoordinator.AcquireCohortStableSlotLease($"main-suspect-release:{tip}", cancellationToken);
                var result = AcceptanceExecutionRunner.RunFocusedVerification(verifier, checkout, goalId: null,
                    selection.Request, lease.Environment.BuildPermitIndex, lease, runBaselineArm: false, cancellationToken,
                    projectHomeDirectory: workspace.ProjectHomeDirectoryOrNull);
                var interpreted = ConductorAcceptanceCohortFocusedAttribution.Interpret(result, selection);
                // TRX paths identify the actual focused execution, even after checkout cleanup.
                receipt = interpreted.TestResultPaths.FirstOrDefault() ?? receipt;
                return interpreted.Kind == ConductorCohortFocusedPassKind.Executed
                    ? new(interpreted.AllChecksPassed ? MainSuspectReleaseProbeOutcome.Passed : MainSuspectReleaseProbeOutcome.Failed, receipt)
                    : new(MainSuspectReleaseProbeOutcome.CouldNotRun, receipt, interpreted.Detail);
            }
            finally { Remove(root, checkout); }
        }

        private static void Remove(string root, string checkout)
        {
            var removed = GitCli.Run(root, "worktree", "remove", "--force", checkout);
            if (!removed.Succeeded || removed.DrainTimedOut)
                throw new InvalidOperationException($"Release probe checkout cleanup failed: exit={removed.ExitCode}; path={checkout}");
        }
    }
}
