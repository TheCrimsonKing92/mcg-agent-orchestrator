using System.Diagnostics;
using System.Text;
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
    IReadOnlyList<string> Projects,
    bool MissingEvidence,
    bool BuildFailed,
    string Diagnostic);

public static class OrchestratorBuildEvidenceCheck
{
    internal const string ScriptRelativePath = "scripts/Invoke-WorkerBuildCheck.ps1";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    internal static OrchestratorBuildEvidenceResolution Resolve(
        string worktreeRoot,
        AgentRole role,
        IEnumerable<string> changedPaths,
        IEnumerable<string> dirtyPaths,
        bool failedWorkerBuildCheck,
        bool hasWorkerBuildEvidence,
        Func<OrchestratorBuildCheckRequest, OrchestratorBuildCheckResult> runBuildCheck)
    {
        var projects = WorkerBuildEvidenceRequirement.FindRequiredProjects(
            worktreeRoot,
            role,
            changedPaths,
            dirtyPaths);
        if (failedWorkerBuildCheck || projects.Count == 0 || hasWorkerBuildEvidence)
        {
            return new(projects, MissingEvidence: false, BuildFailed: false, Diagnostic: string.Empty);
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

        var provenance = $"build_evidence_producer=orchestrator; projects={string.Join(",", projects)}";
        if (!result.Ran)
        {
            return new(
                projects,
                MissingEvidence: true,
                BuildFailed: false,
                Diagnostic: $"{provenance}; no compile verdict produced. {result.Output}".Trim());
        }

        return new(
            projects,
            MissingEvidence: false,
            BuildFailed: result.ExitCode != 0,
            Diagnostic: $"{provenance}; {result.Output}".Trim());
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

        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        try
        {
            var startInfo = WorkerProcessRunner.BuildPowerShellStartInfo(
                BuildCommand(request.Projects),
                request.WorktreeRoot,
                redirectStandardInput: false);
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new(false, -1, "PowerShell did not start the build evidence check.");
            }

            process.OutputDataReceived += (_, args) => AppendLine(standardOutput, args.Data);
            process.ErrorDataReceived += (_, args) => AppendLine(standardError, args.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var timeoutMilliseconds = (int)Math.Clamp(request.Timeout.TotalMilliseconds, 1, int.MaxValue);
            if (!process.WaitForExit(timeoutMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                try { process.WaitForExit(); } catch { }
                return new(false, -1, $"Build evidence check timed out after {request.Timeout}.");
            }

            process.WaitForExit();
            var output = CombineOutput(standardOutput, standardError);
            if (process.ExitCode == 0)
            {
                return output.Contains("PASS build: 0 errors", StringComparison.OrdinalIgnoreCase)
                    ? new(true, 0, output)
                    : new(false, process.ExitCode, $"Build evidence check returned malformed success output. {output}".Trim());
            }

            var isMissingProject = output.Contains("FAIL build: missing project(s)", StringComparison.OrdinalIgnoreCase);
            var hasFailureVerdict = output.Contains(": error ", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("FAIL build:", StringComparison.OrdinalIgnoreCase);
            return !isMissingProject && hasFailureVerdict
                ? new(true, process.ExitCode, output)
                : new(false, process.ExitCode, $"Build evidence check returned no compiler verdict. {output}".Trim());
        }
        catch (Exception exception)
        {
            return new(false, -1, $"Build evidence check failed to launch: {exception.Message}");
        }
    }

    private static string EscapeSingleQuoted(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static void AppendLine(StringBuilder builder, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (builder)
        {
            builder.AppendLine(line);
        }
    }

    private static string CombineOutput(StringBuilder standardOutput, StringBuilder standardError) =>
        $"{standardOutput}{standardError}".Trim();
}
