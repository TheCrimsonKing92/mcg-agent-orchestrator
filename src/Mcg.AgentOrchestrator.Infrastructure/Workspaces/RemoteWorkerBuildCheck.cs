using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record RemoteWorkerBuildCheckSettings(string Mode = "off", int RemoteSeconds = 600, string? FaultReason = null);
internal sealed record RemoteWorkerBuildCheckRequest(string GoalId, string TaskId, string OperationId,
    string WorktreeRoot, string TreeDigest, IReadOnlyList<string> Projects, DateTimeOffset RemoteDeadline);
internal sealed record RemoteWorkerBuildCheckContext(RemoteLaneExecutorConfiguration Configuration,
    IRemoteLaneExecutor Executor, TimeProvider Clock, TimeSpan PollInterval,
    Func<string, Dictionary<string, string>?, string[], WorktreeTreeDigestGitStep> Git);
internal sealed record RemoteWorkerBuildCheckOutcome(string? ExecutorId, string? FallbackReason,
    string? CandidateCommit, string? BuildOutputPath, string? RemoteReceiptPath);

// Remote evidence is advisory until binding, receipt coverage and host translation all agree.
internal static class RemoteWorkerBuildCheck
{
    internal static async Task<RemoteWorkerBuildCheckOutcome> RunAsync(
        RemoteWorkerBuildCheckRequest request, RemoteWorkerBuildCheckContext context)
    {
        string? executorId = null, commit = null;
        RemoteLaneCoordinator? coordinator = null;
        IRemoteLaneHandle? handle = null;
        RemoteWorkerBuildCheckOutcome Fallback(string reason) => new(executorId, reason, commit, null, null);
        try
        {
            var clock = context.Clock;
            if (request.RemoteDeadline - clock.GetUtcNow() < TimeSpan.FromSeconds(60))
                return Fallback("remote-budget-unavailable");
            using var budget = new CancellationTokenSource(request.RemoteDeadline - clock.GetUtcNow(), clock);
            var candidate = await Task.Run(() =>
            {
                var created = RemoteBuildCandidateCommit.TryCreate(request.WorktreeRoot, context.Git, out var sha, out var hash, out _);
                return (Created: created, Commit: sha, Tree: hash);
            }).WaitAsync(budget.Token).ConfigureAwait(false);
            if (!candidate.Created)
                return Fallback("candidate-commit-unavailable");
            commit = candidate.Commit;
            var tree = candidate.Tree;
            if (request.TreeDigest != WorktreeTreeDigest.Algorithm + ":" + tree)
                return Fallback("candidate-tree-mismatch");
            var identity = new RemoteLaneCandidateIdentity("wbc-" + request.OperationId[..12], request.GoalId, commit, tree, "", "worker-build-check:v1");
            coordinator = new(context.Configuration, identity, request.WorktreeRoot, null, context.Executor, clock,
                context.PollInterval, null, claimClass: RemoteExecutorClaimClass.FocusedEvidence);
            string? admissionReason = null;
            var entry = context.Configuration.Enabled ? coordinator.TryClaimIdleExecutor(out admissionReason) : null;
            if (entry is null) return Fallback(admissionReason ?? "no-idle-executor");
            executorId = entry.Id;
            var project = string.Join(";", request.Projects);
            var job = new RemoteLaneRequest(entry.Id, identity.AttemptId, request.GoalId, "worker-build-check", project,
                "", GoalAcceptanceVerifier.ShortHash(project), commit, tree, "", "worker-build-check:v1",
                Kind: "build-check", BuildProjects: request.Projects);
            var remaining = request.RemoteDeadline - clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero) return Fallback("remote-budget-exhausted");
            var submitted = await SubmitAsync(context.Executor, job, budget.Token).ConfigureAwait(false);
            handle = submitted.Handle;
            if (submitted.FailureReason is { } refusal) return Fallback(refusal);
            if (handle is null) return Fallback("transport-unavailable");
            var heartbeat = clock.GetUtcNow();
            while (true)
            {
                if (handle.NewestHeartbeat is { } newest && newest > heartbeat) heartbeat = newest;
                var now = clock.GetUtcNow();
                if (now - heartbeat > TimeSpan.FromSeconds(entry.LeaseSeconds)) return Fallback("lease-expired");
                if (now >= request.RemoteDeadline) return Fallback("remote-budget-exhausted");
                if (handle.TryGetResult() is { } result)
                {
                    if (RemoteLaneCoordinator.BindingMismatch(job, result) is { } mismatch)
                        return Fallback(JsonNamingPolicy.KebabCaseLower.ConvertName(mismatch.ToString()));
                    var output = result.BuildOutputPath is { } path && File.Exists(path) ? File.ReadAllText(path) : "";
                    var classified = OrchestratorBuildEvidenceCheck.ClassifyOutput(0, output);
                    if (result.ExitCode != 0 || !classified.Ran || classified.ExitCode != 0)
                        return Fallback("remote-build-not-success");
                    var reason = ValidateReceipt(result.BuildReceiptPath, request);
                    return reason is null ? new(executorId, null, commit, result.BuildOutputPath, result.BuildReceiptPath) : Fallback(reason);
                }
                remaining = request.RemoteDeadline - clock.GetUtcNow();
                var interval = context.PollInterval > TimeSpan.Zero ? context.PollInterval : TimeSpan.FromSeconds(1);
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(interval < remaining ? interval : remaining, clock, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (context.Clock.GetUtcNow() >= request.RemoteDeadline)
        { return Fallback("remote-budget-exhausted"); }
        catch (Exception) { return Fallback("transport-fault"); }
        finally
        {
            try { handle?.Abandon(); } catch (Exception) { }
            try { if (executorId is not null) coordinator?.Release(executorId); } catch (Exception) { }
            try { coordinator?.Dispose(); } catch (Exception) { }
        }
    }

    private static async Task<RemoteLaneSubmission> SubmitAsync(IRemoteLaneExecutor executor, RemoteLaneRequest job, CancellationToken token)
    {
        var submission = executor.SubmitAsync(job, token);
        try { return await submission.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            // A non-cooperative transport may finish after fallback. It must never leave a live polling handle.
            _ = submission.ContinueWith(completed =>
            {
                _ = completed.Exception;
                try { if (completed.IsCompletedSuccessfully) completed.Result.Handle?.Abandon(); } catch (Exception) { }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    private static string? ValidateReceipt(string? path, RemoteWorkerBuildCheckRequest request)
    {
        if (path is null || !File.Exists(path)) return "remote-receipt-missing";
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var schema) ||
                schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 1 ||
                !root.TryGetProperty("buildOutcome", out var outcome) || outcome.ValueKind != JsonValueKind.String || outcome.GetString() != "success")
                return "remote-receipt-invalid";
            if (!root.TryGetProperty("treeDigest", out var digest) || digest.ValueKind != JsonValueKind.String || digest.GetString() != request.TreeDigest)
                return "remote-receipt-digest-mismatch";
            if (!root.TryGetProperty("projects", out var projects) || projects.ValueKind != JsonValueKind.Array ||
                projects.EnumerateArray().Any(p => p.ValueKind != JsonValueKind.String) ||
                WorkerBuildReceiptCoverage.FindMissingProjects(request.Projects, projects.EnumerateArray().Select(p => p.GetString()!).ToArray()).Count != 0)
                return "remote-receipt-incomplete";
            if (root.TryGetProperty("projectResults", out var results) &&
                (results.ValueKind != JsonValueKind.Array || results.EnumerateArray().Any(r => r.ValueKind != JsonValueKind.Object ||
                    !r.TryGetProperty("exitCode", out var exit) || exit.ValueKind != JsonValueKind.Number || !exit.TryGetInt32(out var code) || code != 0)))
                return "remote-receipt-incomplete";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return "remote-receipt-invalid"; }
    }
}
