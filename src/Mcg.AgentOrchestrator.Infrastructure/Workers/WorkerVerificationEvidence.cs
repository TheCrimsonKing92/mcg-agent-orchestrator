using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class WorkerVerificationEvidence
{
    private const string LegacySnapshotUnavailableReason = "legacy-snapshot-authoritative-output-unavailable";

    public static string RequireAuthoritativeStandardOutput(
        TaskVerificationRecord verification,
        LogicalArtifactIdentity identity)
    {
        if (verification.AuthoritativeStandardOutput is { } authoritativeOutput)
        {
            return authoritativeOutput;
        }

        if (TryRecoverLegacySnapshotStandardOutput(verification, out var recoveredOutput))
        {
            return recoveredOutput;
        }

        throw new WorkerContextPreparationException(
            identity,
            verification.FullStandardOutputUnavailableReason ?? "authoritative-evidence-unavailable",
            "Complete stdout is unavailable; the bounded verification preview is not authoritative evidence.");
    }

    public static bool TryRecoverLegacySnapshotStandardOutput(
        TaskVerificationRecord verification,
        out string output)
    {
        output = string.Empty;
        if (!string.Equals(
                verification.FullStandardOutputUnavailableReason,
                LegacySnapshotUnavailableReason,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(verification.StandardOutputPath))
        {
            return false;
        }

        var recordedPath = verification.StandardOutputPath;
        var resolvedPath = Path.IsPathRooted(recordedPath)
            ? recordedPath
            : Path.Combine(verification.WorkingDirectory, recordedPath);
        try
        {
            var candidate = File.ReadAllText(resolvedPath);
            // A legacy snapshot recorded no digest. Its output file can become authoritative only when
            // the snapshot retained every character; matching a bounded head/tail preview cannot prove
            // that the omitted middle bytes are unchanged.
            if (candidate.Length > VerificationTextBounds.BoundThreshold ||
                !candidate.Equals(verification.StandardOutput, StringComparison.Ordinal))
            {
                return false;
            }

            output = candidate;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
