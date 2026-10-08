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
    // Published by the App's OrchestratorHome accessor before command dispatch.
    internal const string HomeEnvironmentVariable = "MCG_ORCHESTRATOR_HOME";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(7);

    internal static OrchestratorBuildEvidenceResolution Resolve(
        string worktreeRoot,
        AgentRole role,
        IEnumerable<string> changedPaths,
        IEnumerable<string> dirtyPaths,
        bool failedWorkerBuildCheck,
        Func<WorkerBuildReceiptVerdict> workerBuildReceipt,
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
        if (projects.Count == 0)
        {
            return new(MissingEvidence: false, BuildFailed: false, Diagnostic: string.Empty);
        }
        var receipt = workerBuildReceipt();
        if (receipt.Matches)
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
                Diagnostic: $"worker_build_receipt={receipt.Reason}; build_evidence_attempt=orchestrator; {projectsDiagnostic}; no compile verdict produced. {boundedOutput}".Trim());
        }

        return new(
            MissingEvidence: false,
            BuildFailed: result.ExitCode != 0,
            Diagnostic: $"worker_build_receipt={receipt.Reason}; build_evidence_producer=orchestrator; {projectsDiagnostic}; {boundedOutput}".Trim());
    }

    public static string? ResolveScriptPath(Func<string, string?>? readEnvironment = null)
    {
        var home = (readEnvironment ?? Environment.GetEnvironmentVariable)(HomeEnvironmentVariable)?.Trim();
        return string.IsNullOrEmpty(home)
            ? null
            : Path.GetFullPath(Path.Combine(home, ScriptRelativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    public static string FormatScriptInvocation(string? scriptPath) =>
        scriptPath is null
            ? $"[Invoke-WorkerBuildCheck.ps1 unavailable: orchestrator home unresolved ({HomeEnvironmentVariable} unset)]"
            : $"& '{EscapeSingleQuoted(scriptPath)}'";

    public static string BuildCommand(IReadOnlyList<string> projects) =>
        BuildCommand(ResolveScriptPath(), projects);

    internal static string BuildCommand(string? scriptPath, IReadOnlyList<string> projects)
    {
        var arguments = string.Join(" ", projects.Select(project => $"'{EscapeSingleQuoted(project)}'"));
        return $"{FormatScriptInvocation(scriptPath)} {arguments}".TrimEnd();
    }

    public static OrchestratorBuildCheckResult RunDefault(OrchestratorBuildCheckRequest request) =>
        Run(request, Environment.GetEnvironmentVariable,
            launchRequest => Task.Run(() => WorkerProcessRunner.RunBufferedAsync(launchRequest, CancellationToken.None))
                .GetAwaiter().GetResult());

    internal static OrchestratorBuildCheckResult Run(
        OrchestratorBuildCheckRequest request,
        Func<string, string?> readEnvironment,
        Func<WorkerProcessRunRequest, WorkerProcessRunResult> launch)
    {
        var scriptPath = ResolveScriptPath(readEnvironment);
        if (scriptPath is null)
        {
            return new(false, -1, $"Build evidence script not found: orchestrator home unresolved ({HomeEnvironmentVariable} unset)");
        }

        if (!File.Exists(scriptPath))
        {
            return new(false, -1, $"Build evidence script not found: {scriptPath}");
        }

        try
        {
            var result = launch(new WorkerProcessRunRequest(
                BuildCommand(scriptPath, request.Projects),
                request.WorktreeRoot,
                Timeout: request.Timeout));
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
