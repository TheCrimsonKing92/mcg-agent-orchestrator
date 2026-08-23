using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record OrchestratorBuildCheckRequest(
    string WorktreeRoot,
    IReadOnlyList<string> Projects,
    TimeSpan Timeout);

public sealed record OrchestratorBuildCheckResult(
    bool Ran,
    int ExitCode,
    string Output);

internal sealed record OrchestratorBuildEvidenceResolution(
    bool MissingEvidence,
    bool BuildFailed,
    string Diagnostic)
{
    public bool FailsRound => MissingEvidence || BuildFailed;

    public string AppendDiagnostic(string? standardError)
    {
        var combined = Append(standardError, Diagnostic);
        if (BuildFailed)
        {
            combined = Append(
                combined,
                DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed));
        }

        if (MissingEvidence)
        {
            combined = Append(
                combined,
                DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing));
        }

        return combined;
    }

    private static string Append(string? current, string? next) =>
        string.IsNullOrWhiteSpace(next)
            ? current ?? string.Empty
            : string.IsNullOrEmpty(current)
                ? next
                : current.TrimEnd() + Environment.NewLine + next;
}

public static class OrchestratorBuildEvidenceCheck
{
    internal const string ScriptRelativePath = "scripts/Invoke-WorkerBuildCheck.ps1";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(7);

    internal static OrchestratorBuildEvidenceResolution Resolve(
        string worktreeRoot,
        AgentRole role,
        IEnumerable<string> changedPaths,
        IEnumerable<string> dirtyPaths,
        bool failedWorkerBuildCheck,
        Func<bool> hasWorkerBuildEvidence,
        Func<OrchestratorBuildCheckRequest, OrchestratorBuildCheckResult> runBuildCheck)
    {
        if (failedWorkerBuildCheck)
        {
            return new(MissingEvidence: false, BuildFailed: false, Diagnostic: string.Empty);
        }

        var projects = WorkerBuildEvidenceRequirement.FindRequiredProjects(
            worktreeRoot,
            role,
            changedPaths,
            dirtyPaths);
        if (projects.Count == 0 || hasWorkerBuildEvidence())
        {
            return new(MissingEvidence: false, BuildFailed: false, Diagnostic: string.Empty);
        }

        OrchestratorBuildCheckResult result;
        try
        {
            result = runBuildCheck(new(worktreeRoot, projects, DefaultTimeout));
        }
        catch (Exception exception)
        {
            result = new(false, -1, $"Orchestrator build evidence check could not run: {exception.Message}");
        }

        var projectsDiagnostic = $"projects={string.Join(",", projects)}";
        var boundedOutput = VerificationTextBounds.BoundText(result.Output.Trim(), path: null);
        if (!result.Ran)
        {
            return new(
                MissingEvidence: true,
                BuildFailed: false,
                Diagnostic: $"build_evidence_attempt=orchestrator; {projectsDiagnostic}; no compile verdict produced. {boundedOutput}".Trim());
        }

        return new(
            MissingEvidence: false,
            BuildFailed: result.ExitCode != 0,
            Diagnostic: $"build_evidence_producer=orchestrator; {projectsDiagnostic}; {boundedOutput}".Trim());
    }

    public static string BuildCommand(IReadOnlyList<string> projects)
    {
        var arguments = string.Join(" ", projects.Select(project => $"'{EscapeSingleQuoted(project)}'"));
        return $"& './{ScriptRelativePath}' {arguments}".TrimEnd();
    }

    public static OrchestratorBuildCheckResult RunDefault(OrchestratorBuildCheckRequest request)
    {
        var scriptPath = Path.Combine(
            request.WorktreeRoot,
            ScriptRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(scriptPath))
        {
            return new(false, -1, $"Build evidence script not found: {ScriptRelativePath}");
        }

        try
        {
            var result = Task.Run(() => WorkerProcessRunner.RunBufferedAsync(
                    new WorkerProcessRunRequest(
                        BuildCommand(request.Projects),
                        request.WorktreeRoot,
                        Timeout: request.Timeout),
                    CancellationToken.None))
                .GetAwaiter()
                .GetResult();
            return ClassifyOutput(result.ExitCode, CombineOutput(result.StandardOutput, result.StandardError));
        }
        catch (OperationCanceledException)
        {
            return new(false, -1, $"Build evidence check timed out after {request.Timeout}.");
        }
        catch (Exception exception)
        {
            return new(false, -1, $"Build evidence check failed to launch: {exception.Message}");
        }
    }

    internal static OrchestratorBuildCheckResult ClassifyOutput(int exitCode, string output)
    {
        if (exitCode == 0)
        {
            return output.Contains("PASS build: 0 errors", StringComparison.OrdinalIgnoreCase)
                ? new(true, 0, output)
                : new(false, exitCode, $"Build evidence check returned malformed success output. {output}".Trim());
        }

        var isMissingProject = output.Contains("FAIL build: missing project(s)", StringComparison.OrdinalIgnoreCase);
        var hasFailureVerdict = output.Contains(": error ", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("FAIL build:", StringComparison.OrdinalIgnoreCase);
        return !isMissingProject && hasFailureVerdict
            ? new(true, exitCode, output)
            : new(false, exitCode, $"Build evidence check returned no compiler verdict. {output}".Trim());
    }

    private static string EscapeSingleQuoted(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string CombineOutput(string standardOutput, string standardError) =>
        $"{standardOutput}{Environment.NewLine}{standardError}".Trim();
}
