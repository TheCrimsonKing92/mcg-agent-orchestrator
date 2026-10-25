using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record CompletionBuildPlacementResult(CompletionBuildEvidencePhase Phase, int BuildExitCode,
    string Diagnostic, string StandardOutput, string StandardError);

internal static class CompletionBuildPlacement
{
    internal static CompletionBuildPlacementResult Run(CompletionBuildEvidenceOperation operation, string receiptPath,
        string ledgerPath, RemoteWorkerBuildCheckSettings settings,
        Func<RemoteWorkerBuildCheckRequest, Task<RemoteWorkerBuildCheckOutcome>> remote,
        Func<TimeSpan, CompletionBuildPlacementResult> local, TimeProvider clock)
    {
        if (settings.Mode != "remote-first") return local(operation.Deadline - clock.GetUtcNow());
        var started = clock.GetUtcNow();
        RemoteWorkerBuildCheckOutcome outcome;
        try
        {
            outcome = remote(new(operation.GoalId, operation.TaskId, operation.Id, operation.WorktreeRoot,
                operation.TreeDigest, operation.Projects, operation.Deadline - OrchestratorBuildEvidenceCheck.DefaultTimeout))
                .GetAwaiter().GetResult();
        }
        catch (Exception) { outcome = new(null, "transport-fault", null, null, null); }
        var seconds = Math.Round((clock.GetUtcNow() - started).TotalSeconds, 1);
        CompletionBuildPlacementResult? bound = null;
        if (outcome.FallbackReason is null)
        {
            bound = Translate(operation, receiptPath, outcome, clock);
            if (bound is null) outcome = outcome with { FallbackReason = "host-receipt-unverified" };
        }
        AppendLedger(ledgerPath, operation, outcome, clock, seconds);
        if (bound is not null) return bound;
        var result = local(operation.Deadline - clock.GetUtcNow());
        return result with { Diagnostic = "build_evidence_host=local; remote_fallback=" + outcome.FallbackReason + "; " + result.Diagnostic };
    }

    internal static CompletionBuildPlacementResult RunLocalWithExecutionQos(CompletionBuildEvidenceOperation operation,
        string statePath, string receiptPath, TimeSpan remaining, Func<WorkerProcessRunResult> runBuild,
        Func<int, IReadOnlyList<int>?>? members = null, Func<int, uint, uint, bool>? setter = null,
        TimeSpan? interval = null)
    {
        using var qos = BuildLaunchQosSweep.Begin("completion-build", statePath, operation.GoalId,
            Environment.ProcessId, members ?? BuildLaunchQosSweep.RootAndDescendants, setter, interval);
        return RunLocal(operation, receiptPath, remaining, runBuild);
    }

    internal static CompletionBuildPlacementResult RunLocal(CompletionBuildEvidenceOperation operation,
        string receiptPath, TimeSpan remaining, Func<WorkerProcessRunResult> runBuild)
    {
        var run = runBuild();
        var classified = OrchestratorBuildEvidenceCheck.ClassifyOutput(run.ExitCode, run.StandardOutput + Environment.NewLine + run.StandardError);
        if (classified.Ran && classified.ExitCode == 0)
        {
            var receipt = WorkerBuildReceipt.Evaluate(receiptPath, operation.WorktreeRoot);
            if (!receipt.Matches || WorkerBuildReceiptCoverage.FindMissingProjects(operation.Projects, receipt.Projects).Count != 0)
                classified = new(false, run.ExitCode, "Build helper did not produce exact required receipt coverage. " + classified.Output);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(operation.ResultPath)!);
        File.WriteAllText(operation.ResultPath + ".stdout", run.StandardOutput);
        File.WriteAllText(operation.ResultPath + ".stderr", run.StandardError);
        return new(!classified.Ran ? CompletionBuildEvidencePhase.ApparatusFailure : classified.ExitCode == 0
                ? CompletionBuildEvidencePhase.Success : CompletionBuildEvidencePhase.CompilerFailure,
            run.ExitCode, VerificationTextBounds.BoundText(classified.Output, path: operation.ResultPath + ".stdout"),
            run.StandardOutput, run.StandardError);
    }

    internal static WorkerProcessRunResult DefaultLocalBuild(CompletionBuildEvidenceOperation operation, TimeSpan remaining)
    {
        var script = OrchestratorBuildEvidenceCheck.ResolveScriptPath();
        if (script is null || !File.Exists(script)) throw new FileNotFoundException("Sanctioned build helper is unavailable.");
        var run = WorkerProcessRunner.RunBufferedAsync(new WorkerProcessRunRequest(
            OrchestratorBuildEvidenceCheck.BuildCommand(script, operation.Projects), operation.WorktreeRoot,
            Timeout: remaining, RequireOwnedProcessGroup: true), CancellationToken.None).GetAwaiter().GetResult();
        return run;
    }

    private static CompletionBuildPlacementResult? Translate(CompletionBuildEvidenceOperation operation,
        string receiptPath, RemoteWorkerBuildCheckOutcome outcome, TimeProvider clock)
    {
        var written = false;
        var temporary = receiptPath + "." + operation.Id + ".tmp";
        byte[]? previous = null;
        try
        {
            previous = File.Exists(receiptPath) ? File.ReadAllBytes(receiptPath) : null;
            var output = File.ReadAllText(outcome.BuildOutputPath!);
            Directory.CreateDirectory(Path.GetDirectoryName(operation.ResultPath)!);
            File.Copy(outcome.BuildOutputPath!, operation.ResultPath + ".stdout", overwrite: true);
            File.WriteAllText(operation.ResultPath + ".stderr", "");
            var source = JsonNode.Parse(File.ReadAllText(outcome.RemoteReceiptPath!))!.AsObject();
            var results = new JsonArray();
            foreach (var project in operation.Projects)
            {
                var entry = source["projectResults"]?.AsArray().FirstOrDefault(r =>
                    string.Equals(r?["project"]?.GetValue<string>()?.Replace('\\', '/'), project.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
                var row = entry?.DeepClone().AsObject() ?? new JsonObject();
                row["project"] = project;
                row["exitCode"] = 0;
                row["logPath"] = operation.ResultPath + ".stdout";
                results.Add(row);
            }
            var receipt = new JsonObject
            {
                ["schemaVersion"] = 1, ["treeDigest"] = operation.TreeDigest, ["finalTreeDigest"] = operation.TreeDigest,
                ["digestAlgorithm"] = WorktreeTreeDigest.Algorithm, ["buildOutcome"] = "success",
                ["generatedAtUtc"] = clock.GetUtcNow().ToString("O"), ["worktreeRoot"] = operation.WorktreeRoot,
                ["configuration"] = "Debug", ["projects"] = JsonSerializer.SerializeToNode(operation.Projects),
                ["projectResults"] = results, ["executorId"] = outcome.ExecutorId, ["candidateCommit"] = outcome.CandidateCommit
            };
            if (File.Exists(WorkerBuildProjectScope.MetadataPath(operation.WorktreeRoot)))
            {
                var scope = WorkerBuildProjectScope.Resolve(operation.WorktreeRoot, []);
                receipt["scopeMetadataDigest"] = scope.MetadataDigest;
                receipt["scopeStableDigest"] = scope.StableMetadataDigest;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
            File.WriteAllText(temporary, receipt.ToJsonString());
            File.Move(temporary, receiptPath, overwrite: true);
            written = true;
            var verdict = WorkerBuildReceipt.Evaluate(receiptPath, operation.WorktreeRoot);
            if (verdict.Matches && WorkerBuildReceiptCoverage.FindMissingProjects(operation.Projects, verdict.Projects).Count == 0)
                return new(CompletionBuildEvidencePhase.Success, 0,
                    $"build_evidence_host=remote:{outcome.ExecutorId}; candidate_commit={outcome.CandidateCommit}; " +
                    VerificationTextBounds.BoundText(output, path: operation.ResultPath + ".stdout"), output, "");
        }
        catch (Exception) { /* A translation fault must fall back to the local verdict. */ }
        finally { try { File.Delete(temporary); } catch (Exception) { } }
        if (written)
        {
            File.Delete(receiptPath);
            if (previous is not null) File.WriteAllBytes(receiptPath, previous);
        }
        return null;
    }

    private static void AppendLedger(string path, CompletionBuildEvidenceOperation operation,
        RemoteWorkerBuildCheckOutcome outcome, TimeProvider clock, double seconds)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream);
            writer.WriteLine(JsonSerializer.Serialize(new
            {
                at = clock.GetUtcNow().ToString("O"), goal_id = operation.GoalId, task_id = operation.TaskId,
                operation_id = operation.Id, placement = outcome.FallbackReason is null ? "remote" : "local",
                executor_id = outcome.ExecutorId, fallback_reason = outcome.FallbackReason,
                candidate_commit = outcome.CandidateCommit, tree_digest = operation.TreeDigest, remote_seconds = seconds
            }));
        }
        catch (Exception) { /* Placement telemetry cannot change the build verdict. */ }
    }
}
