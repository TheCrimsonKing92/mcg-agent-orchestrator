using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ImpactReplay
{
    internal static ImpactReplayReport Read(string executionDirectory, string orchestratorDirectory,
        DateTimeOffset? since = null, int limit = 100, IReadOnlyList<string>? floor = null,
        string? tempCheckoutParent = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var skipped = new List<ImpactReplayDiagnostic>();
        var unreplayable = new List<ImpactReplayDiagnostic>();
        var cleanupErrors = new List<string>();
        var records = new List<ImpactReplayAttemptRecord>();
        var floorNames = new SortedSet<string>(floor ?? [], StringComparer.Ordinal);
        foreach (var attempt in ImpactReplayAttemptSource.Read(orchestratorDirectory, since, skipped))
        {
            if (records.Count + unreplayable.Count >= limit) break;
            string? checkout = null;
            var registered = false;
            try
            {
                var diff = GitCli.RunForRepository(executionDirectory, "diff", "--name-only", "-z", "--no-renames",
                    attempt.MainHeadSha, attempt.BranchHeadSha, "--");
                if (!diff.ProcessStarted || !diff.Succeeded || diff.DrainTimedOut) throw new InvalidDataException($"Git diff failed: exit={diff.ExitCode}; {diff.Error}");
                var changedFiles = diff.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
                tempCheckoutParent ??= OrchestratorTempRoot.GetPurposeDirectory("impact-replay-worktrees");
                Directory.CreateDirectory(tempCheckoutParent);
                checkout = Path.GetFullPath(Path.Combine(tempCheckoutParent, Guid.NewGuid().ToString("N")));
                // Detached checkouts only supply source text. Hooks must not mutate the live repository.
                var added = GitCli.RunForRepository(executionDirectory, "-c", "core.hooksPath=" + Path.Combine(checkout, "no-hooks"),
                    "worktree", "add", "--detach", "--quiet", checkout, attempt.BranchHeadSha);
                registered = added.ProcessStarted && added.Succeeded && !added.DrainTimedOut;
                if (!registered) throw new InvalidDataException($"Detached checkout unavailable: exit={added.ExitCode}; {added.Error}");
                var manifest = AcceptanceManifestLocator.Resolve(checkout);
                if (!File.Exists(manifest)) throw new InvalidDataException("Historical manifest is missing.");
                var settings = AcceptanceGateEngineSettings.Load(checkout);
                var checkNames = settings.DeclaredDotnetTestChecks.Where(check => check.Project is { } project &&
                        Path.GetFileName(project.Replace('\\', '/')).Equals("Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", StringComparison.OrdinalIgnoreCase))
                    .Select(check => check.Name).ToArray();
                var classes = AcceptanceTestClassSourceScanner.Scan(checkout);
                var lanes = AcceptanceLaneMembership.ResolveOwnedCollections(settings.InfrastructureTestLanes, classes);
                var evidence = ImpactReplayTrxEvidence.Read(attempt, lanes, checkNames, classes);
                if (evidence.LaneOutcomes.Count == 0)
                {
                    skipped.Add(new(attempt.AttemptId, "No Infrastructure.Tests lane evidence."));
                    continue;
                }
                var attemptFloor = lanes.Where(lane => floor is null ? IsDefaultFloor(lane.Name) : floor.Contains(lane.Name, StringComparer.Ordinal))
                    .Select(lane => lane.Name).Order(StringComparer.Ordinal).ToArray();
                floorNames.UnionWith(attemptFloor);
                var selection = ReverseDependencyTestImpactReader.Read(checkout,
                    changedFiles.Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToArray(), bypassCache: true);
                var selectedLanes = new HashSet<string>(attemptFloor, StringComparer.Ordinal);
                var unmapped = false;
                foreach (var simpleName in selection.TestClassNames)
                {
                    var matches = classes.Where(item => item.FullName.Split('.', '+').Last() == simpleName).ToArray();
                    if (matches.Length == 0) unmapped = true;
                    foreach (var item in matches)
                    {
                        var memberships = AcceptanceLaneMembership.LanesIncluding(lanes, item.FullName);
                        if (memberships.Count == 0) unmapped = true;
                        selectedLanes.UnionWith(memberships.Select(lane => lane.Name));
                    }
                }
                var resolved = selection.Outcome == ReverseDependencySelectionOutcome.Resolved && !unmapped;
                var ran = evidence.LaneOutcomes.Select(item => item.Lane).Order(StringComparer.Ordinal).ToArray();
                if (!resolved) selectedLanes.UnionWith(ran);
                var skippedLanes = ran.Where(lane => !selectedLanes.Contains(lane)).ToArray();
                var warnings = evidence.Warnings.ToList();
                if (unmapped) warnings.Add("Selected classes could not all be mapped to lanes; no lanes were skipped.");
                records.Add(new(attempt.AttemptId, attempt.GoalId, attempt.StartedAt, attempt.MainHeadSha,
                    attempt.BranchHeadSha, unmapped ? "Abandoned" : selection.Outcome.ToString(),
                    unmapped ? "UnmappedTestClass" : selection.DegradationKind?.ToString() ?? (resolved ? null : selection.Outcome.ToString()),
                    selection.Reason, changedFiles, selection.TestClassNames, attemptFloor, ran,
                    selectedLanes.Order(StringComparer.Ordinal).ToArray(), skippedLanes,
                    lanes.Where(lane => skippedLanes.Contains(lane.Name, StringComparer.Ordinal)).Sum(lane => lane.EstimatedSerialSeconds),
                    evidence.LaneOutcomes, evidence.Failures.Select(failure => failure with
                    { WouldRun = failure.Lane is null ? null : selectedLanes.Contains(failure.Lane) }).ToArray(),
                    warnings.Count == 0, warnings));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or InvalidDataException)
            { unreplayable.Add(new(attempt.AttemptId, ex.Message)); }
            finally
            {
                if (checkout is not null)
                {
                    // Worktree add may register its path before reporting a pipe-drain or checkout fault.
                    var listed = GitCli.RunForRepository(executionDirectory, "worktree", "list", "--porcelain");
                    var stillRegistered = listed.Output.Split('\n').Any(line => line.TrimEnd('\r') == "worktree " + checkout.Replace('\\', '/'));
                    if (registered || stillRegistered || Directory.Exists(checkout))
                    {
                        var removed = GitCli.RunForRepository(executionDirectory, "worktree", "remove", "--force", checkout);
                        if (!removed.ProcessStarted || !removed.Succeeded || removed.DrainTimedOut)
                            cleanupErrors.Add($"Impact replay checkout cleanup failed: exit={removed.ExitCode}; path={checkout}");
                    }
                }
            }
        }
        return new(floor is null ? "default" : "override", floorNames.ToArray(), records, skipped, unreplayable, cleanupErrors);
    }

    private static bool IsDefaultFloor(string name) =>
        name.StartsWith("Process spawning", StringComparison.Ordinal) ||
        name.StartsWith("Isolated process spawning", StringComparison.Ordinal) ||
        name is "Chaos gate" or "Remainder merge train and ratchet";
}
