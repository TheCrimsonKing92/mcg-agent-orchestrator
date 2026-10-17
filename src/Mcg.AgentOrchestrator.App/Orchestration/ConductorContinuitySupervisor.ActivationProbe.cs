using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorContinuitySupervisor
{
    private readonly Func<string, string, IReadOnlyList<string>>? _changedPathsBetween = changedPathsBetween;

    private ActivationAdmissionProbeTracker? ResolveAdmissionProbe(string directory, string from, string to)
    {
        try
        {
            IReadOnlyList<string> paths;
            if (_changedPathsBetween is { } read) paths = read(from, to);
            else
            {
                if (!ConductorGitRevisionReader.IsValid(from) || !ConductorGitRevisionReader.IsValid(to)) return null;
                var diff = GitCli.Run(directory, 30_000, "diff", "--name-only", "--no-renames", from, to, "--");
                if (diff.ExitCode != 0) return null;
                paths = diff.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            }
            return AcceptanceAdmissionPathSet.Touches(paths) ? new(_activationHealthyTicks) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    private void RecordProbeSkipped(string reason, ConductorActivationBuild candidate, ConductorActivationBuild previous)
    {
        var line = $"ACTIVATION_PROBE_SKIPPED reason={reason} candidateCommit={candidate.CommitSha} previousCommit={previous.CommitSha}";
        TryAppendConductEvent(line);
        eventStore.AppendAsync(new RunEventAppend(RunEventTypes.ConductorSupervision, null,
            "activation", "probe-skipped", line, null, _timeProvider.GetUtcNow())).GetAwaiter().GetResult();
    }
}
