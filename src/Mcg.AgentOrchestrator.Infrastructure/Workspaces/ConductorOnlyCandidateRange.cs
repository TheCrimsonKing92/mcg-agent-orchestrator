namespace Mcg.AgentOrchestrator.Infrastructure;

// Proves first-parent provenance and goal patch equivalence against a pinned integration commit.
public static class ConductorOnlyCandidateRange
{
    private const int GitTimeoutMilliseconds = 10_000;

    public static bool IsConductorOnly(string executionDirectory, string integrationBranch,
        string reviewedCommit, string landingCandidate)
    {
        if (!Directory.Exists(executionDirectory) || string.IsNullOrWhiteSpace(integrationBranch) ||
            string.IsNullOrWhiteSpace(reviewedCommit) || string.IsNullOrWhiteSpace(landingCandidate)) return false;
        try
        {
            if (!Resolve(executionDirectory, reviewedCommit, out var reviewed) ||
                !Resolve(executionDirectory, landingCandidate, out var landing) ||
                !Resolve(executionDirectory, $"refs/heads/{integrationBranch}", out var integration) ||
                !Read(executionDirectory, ["log", "--first-parent", "--max-count=201",
                    "--format=%H%x1f%cn%x1f%ce", landing], out var history)) return false;

            var reached = false;
            foreach (var line in history.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = line.TrimEnd('\r').Split('\u001f');
                if (fields.Length != 3 || !IsSha(fields[0])) return false;
                if (string.Equals(fields[0], reviewed, StringComparison.OrdinalIgnoreCase))
                {
                    reached = true;
                    break;
                }
                if (!(fields[1] == "mcg-orchestrator" && fields[2] == "mcg-orchestrator@localhost") &&
                    !(fields[1] == "MCG Acceptance Gate" && fields[2] == "acceptance-gate@localhost")) return false;
            }
            if (!reached || !MergeBase(executionDirectory, integration, reviewed, out var oldBase) ||
                !MergeBase(executionDirectory, integration, landing, out var newBase)) return false;

            // Retightening is the sole path exception. Reuse the existing monotonic row comparison.
            if (!GoalWorktrees.TryReadChangedLines(executionDirectory, oldBase, reviewed, out var oldRatchet,
                    SourceSizeRatchet.SourcePath) ||
                !GoalWorktrees.TryReadChangedLines(executionDirectory, newBase, landing, out var newRatchet,
                    SourceSizeRatchet.SourcePath) ||
                !IntegrationOnlyCandidateDrift.CompareChanges(oldRatchet, newRatchet)) return false;

            string[] paths = ["--", ".", $":(exclude){SourceSizeRatchet.SourcePath}"];
            return GoalWorktrees.TryComputeStablePatchId(executionDirectory, oldBase, reviewed, out var oldPatch, out _, paths) &&
                GoalWorktrees.TryComputeStablePatchId(executionDirectory, newBase, landing, out var newPatch, out _, paths) &&
                string.Equals(oldPatch, newPatch, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // Git inspection that cannot establish the proof grants no candidate-match exception.
            return false;
        }
    }

    private static bool Resolve(string directory, string revision, out string sha) =>
        Read(directory, ["rev-parse", "--verify", "--end-of-options", $"{revision}^{{commit}}"], out sha) && IsSha(sha);

    private static bool MergeBase(string directory, string integration, string revision, out string sha) =>
        Read(directory, ["merge-base", "--all", integration, revision], out sha) && IsSha(sha);

    private static bool IsSha(string value) => value.Length == 40 && value.All(Uri.IsHexDigit);

    private static bool Read(string directory, string[] arguments, out string value)
    {
        var result = GitCli.Run(directory, GitTimeoutMilliseconds, arguments);
        value = result.Output.TrimEnd('\r', '\n');
        return result.Succeeded && result.ProcessStarted && !result.DrainTimedOut &&
            value.Length > 0 && !value.Contains('\uFFFD');
    }
}
