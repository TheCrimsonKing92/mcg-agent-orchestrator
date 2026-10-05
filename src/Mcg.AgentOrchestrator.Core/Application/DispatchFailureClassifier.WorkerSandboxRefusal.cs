using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

public static partial class DispatchFailureClassifier
{
    private const string WorkerSandboxRefusedPrefix = "WORKER_SANDBOX_REFUSED ";

    // Accept only the bare wire line or the exact frame used by RecordHostFailure.
    // An inner UnauthorizedAccessException belongs to this refusal, not provider login.
    [GeneratedRegex(@"^\s*(?:\[dispatch-host\] worker launch/run failed: Mcg\.AgentOrchestrator\.Infrastructure\.WorkerSandboxRefusedException: )?" +
        WorkerSandboxRefusedPrefix + @"reason=(?<reason>\S+)(?: path=(?<path>.*?))?(?: ---> .*)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex WorkerSandboxRefusalLineRegex();

    private static bool TryClassifyWorkerSandboxRefusal(
        TaskSpec task,
        TaskVerificationRecord verification,
        bool workerResultPresent,
        bool hasCommittedChanges,
        out DispatchOutcome outcome)
    {
        outcome = null!;
        if (verification.Succeeded || workerResultPresent)
        {
            return false;
        }

        // Stderr is the guard's stream; prefer its first match over stdout evidence.
        var lines = EnumerateEvidenceLines(verification, includeStandardOutput: false, includeStandardError: true)
            .Concat(EnumerateEvidenceLines(verification, includeStandardOutput: true, includeStandardError: false));
        foreach (var line in lines)
        {
            var match = WorkerSandboxRefusalLineRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var reason = match.Groups["reason"].Value;
            var path = match.Groups["path"];
            var summary = path.Success
                ? $"worker sandbox refused: reason={reason} path={path.Value}"
                : $"worker sandbox refused: reason={reason}; refusal={TruncateEvidence(line)}";
            outcome = BuildOutcome(
                TaskOutcomeRules.WorkerSandboxRefused,
                task,
                verification,
                workerResultPresent,
                hasCommittedChanges,
                new DispatchOutcome(
                    DispatchOutcomeKind.UnknownFailure,
                    verification.ExitCode,
                    HasZeroByteStandardOutput(verification),
                    null,
                    null,
                    RecoveryRecommendation.OperatorNeeded,
                    summary));
            return true;
        }

        return false;
    }
}
