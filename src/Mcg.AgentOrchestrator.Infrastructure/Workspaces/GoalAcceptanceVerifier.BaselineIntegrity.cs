namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    private string? TryLabelFocusedEvidenceBaselineWorktree(string baselinePath)
    {
        var labeler = _testOverrides.BaselineIntegrityLabelerForTests;
        if (labeler is null)
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            labeler = new IcaclsIntegrityLabeler();
        }

        Exception? labelError = null;
        try
        {
            if (labeler.SetIntegrity(
                    baselinePath, WorkerSandboxPreparer.LowInheritableLevel, recursive: true))
            {
                return null;
            }
        }
        catch (Exception error)
        {
            labelError = error;
        }

        try
        {
            var state = labeler.Query(baselinePath);
            if (state.Exists && state.Low && state.Inheritable)
            {
                return null;
            }
        }
        catch (Exception error)
        {
            labelError ??= error;
        }

        var detail = labelError is null ? string.Empty : $" ({labelError.GetType().Name})";
        return $"baseline worktree integrity labeling failed: could not apply inheritable Low integrity label to '{baselinePath}'{detail}";
    }
}
