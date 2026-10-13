using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// Parameterized completion replay for an already validated, isolated copy of acceptance evidence.
/// No store discovery, worker dispatch, or live attempt mutation. Receipt copying/binding is the caller's job.
/// </summary>
internal static class AcceptanceAttributionReplayFixture
{
    internal const string InputVariable = "MCG_ACCEPTANCE_ATTRIBUTION_REPLAY_INPUT";
    internal sealed record CopiedFile(string File, string Sha256);
    internal sealed record CopiedAttemptInput(string SourceRoot, string FinalCandidateSha,
        CopiedFile Attempt, CopiedFile Result, CopiedFile OriginalTrx, CopiedFile RerunTrx,
        CopiedFile OriginalHeartbeat, CopiedFile RerunHeartbeat);

    internal sealed record Receipt(ConductorAdvanceOutcome Outcome, int FailureDecisions,
        bool NextGoalAdvanced, int SourceEnumerations, int SourceReads,
        double AttributionMilliseconds, double CompletionMilliseconds, string? CandidateSha, string? MainSha);

    internal static Receipt ReplayCopiedAttempt(string inputPath)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        var input = JsonSerializer.Deserialize<CopiedAttemptInput>(File.ReadAllText(inputPath), options) ??
            throw new InvalidOperationException("Replay input is missing.");
        ArgumentException.ThrowIfNullOrWhiteSpace(input.FinalCandidateSha);
        var copyRoot = Path.GetDirectoryName(Path.GetFullPath(inputPath))!;
        byte[] ReadCopy(CopiedFile file)
        {
            var path = Path.GetFullPath(Path.Combine(copyRoot, file.File));
            if (Path.IsPathRooted(file.File) || !path.StartsWith(copyRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                new FileInfo(path).Length > 100_000_000)
                throw new InvalidOperationException("Replay receipt is outside the bounded isolated copy.");
            var bytes = File.ReadAllBytes(path);
            if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Replay receipt hash mismatch.");
            return bytes;
        }
        var attempt = JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(ReadCopy(input.Attempt), options)!;
        var result = JsonSerializer.Deserialize<ConductorParallelAcceptanceRunArtifact>(ReadCopy(input.Result), options)!;
        var summary = result.Acceptance ?? throw new InvalidOperationException("Replay lacks acceptance evidence.");
        if (result.Kind != "accepted" || summary.Passed || summary.BranchHeadSha != attempt.BranchHeadSha ||
            summary.MainHeadSha != attempt.MainHeadSha || result.BranchHeadSha != attempt.BranchHeadSha ||
            result.MainHeadSha != attempt.MainHeadSha)
            throw new InvalidOperationException("Replay candidate/attempt identity mismatch.");
        var originalPath = Path.Combine(copyRoot, input.OriginalTrx.File);
        var rerunPath = Path.Combine(copyRoot, input.RerunTrx.File);
        var originalBytes = ReadCopy(input.OriginalTrx);
        var rerunBytes = ReadCopy(input.RerunTrx);
        using var originalStream = new MemoryStream(originalBytes);
        using var rerunStream = new MemoryStream(rerunBytes);
        var originalRun = XDocument.Load(originalStream).Root?.Attribute("id")?.Value;
        var rerunRun = XDocument.Load(rerunStream).Root?.Attribute("id")?.Value;
        if (string.IsNullOrWhiteSpace(originalRun) || string.IsNullOrWhiteSpace(rerunRun) || originalRun == rerunRun)
            throw new InvalidOperationException("Replay original and rerun TRX identities are missing or equal.");
        var originalHeartbeat = JsonSerializer.Deserialize<GateHeartbeatSnapshot>(ReadCopy(input.OriginalHeartbeat), options)!;
        var rerunHeartbeat = JsonSerializer.Deserialize<GateHeartbeatSnapshot>(ReadCopy(input.RerunHeartbeat), options)!;
        if (originalHeartbeat.GoalId != attempt.GoalId || rerunHeartbeat.GoalId != attempt.GoalId ||
            originalHeartbeat.RunId != attempt.AttemptId || rerunHeartbeat.RunId != attempt.AttemptId ||
            originalHeartbeat.ExitCode != 2 || rerunHeartbeat.ExitCode != 0 ||
            originalHeartbeat.ChildPid == rerunHeartbeat.ChildPid)
            throw new InvalidOperationException("Replay heartbeat invocation binding mismatch.");
        var originalDecision = AcceptanceShardCompletionAdjudicator.InspectTrxCompletion([originalPath]);
        var rerunDecision = AcceptanceShardCompletionAdjudicator.InspectTrxCompletion([rerunPath]);
        if (!originalDecision.AssemblyCleanupOnly || originalDecision.Passed || !rerunDecision.Passed)
            throw new InvalidOperationException("Replay does not have the recorded cleanup/probe shape.");
        var originalChecks = summary.RequiredUnmetCriteria.Where(check =>
            check.TestResultAttemptId == attempt.AttemptId && check.TestResultRunOrdinal == 0 &&
            check.TestResultPaths?.Any(path => Path.GetFileName(path) == Path.GetFileName(originalPath)) == true &&
            check.CompletionDecision?.FailedPredicate == AcceptanceShardCompletionPredicates.AssemblyCleanupFailure).ToArray();
        if (originalChecks.Length != 1 || originalHeartbeat.CurrentTarget != originalChecks[0].Name ||
            rerunHeartbeat.CurrentTarget != originalChecks[0].Name)
            throw new InvalidOperationException("Replay original partition binding is ambiguous.");
        var originalCheck = originalChecks[0];
        var copiedCheck = AcceptanceAssemblyCleanupAttributionReader.Attach(originalCheck with
        {
            TestResultPaths = [originalPath], ProcessStderrPath = null,
            GateHeartbeatPath = Path.Combine(copyRoot, input.OriginalHeartbeat.File)
        }, attempt.GoalId, summary.BranchHeadSha, summary.MainHeadSha);
        if (copiedCheck.AssemblyCleanupAttribution is null)
            throw new InvalidOperationException("Replay original cleanup proof is unavailable.");
        summary = new(summary.Passed, summary.UnmetCriteria.Select(check => check == originalCheck ? copiedCheck : check).ToArray(),
            FailureDetail: null, FailedChecks: summary.FailedChecks, BranchHeadSha: summary.BranchHeadSha,
            MainHeadSha: summary.MainHeadSha, TestResultPaths: [originalPath, rerunPath], CheckAttributions: summary.CheckAttributions,
            BaselineAttestation: summary.BaselineAttestation);
        var isolatedGoal = new Goal(new GoalId(attempt.GoalId), "isolated recorded acceptance replay", []);
        var receipt = Complete(isolatedGoal, summary, input.SourceRoot,
            Path.Combine(copyRoot, Guid.NewGuid().ToString("N") + ".census.jsonl"), attempt.ScopePaths ?? []);
        Console.WriteLine(JsonSerializer.Serialize(new { replayKind = "actual-copy", finalCandidateSha = input.FinalCandidateSha,
            historicalAttemptId = attempt.AttemptId, originalTrxRunId = originalRun, rerunTrxRunId = rerunRun, receipt }, options));
        return receipt;
    }

    internal static Receipt Complete(Goal goal, AcceptanceVerificationSummary acceptance,
        string finalCandidateSourceRoot, string isolatedCensusPath, IReadOnlyList<string> changedPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalCandidateSourceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(isolatedCensusPath);
        var index = new AcceptanceFailingTestIndex(isolatedCensusPath);
        AcceptanceTestSourceResolver.Request? request = null;
        var gate = new ApparatusRedGate(index, _ => finalCandidateSourceRoot,
            createSourceRequest: root => request = new(root));
        var failureDecisions = 0;
        var nextGoalAcceptanceRuns = 0;
        var (nextKernel, nextGoal) = ConductorDriverTests.SimpleGoal("isolated replay subsequent goal");
        ConductorDriverTests.PassVerification(nextKernel, nextGoal, nextGoal.Tasks.Single());
        var driver = ConductorDriverTests.MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ =>
            {
                nextGoalAcceptanceRuns++;
                return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
            },
            recordAcceptanceFailure: (observed, _, candidate, main, _, _) =>
            {
                if (observed.Id != goal.Id || candidate != acceptance.BranchHeadSha || main != acceptance.MainHeadSha)
                    throw new InvalidOperationException("Replay completion lost its candidate identity.");
                failureDecisions++;
            },
            getLandingFileScopes: _ => changedPaths,
            apparatusRedGate: gate);
        var candidate = new ConductorParallelAcceptanceCandidate(goal, 0, [], [], [], [],
            acceptance.BranchHeadSha, acceptance.MainHeadSha);
        // Isolate attribution measurement from the full completion; use a separate census and request.
        var attributionGate = new ApparatusRedGate(isolatedCensusPath + ".attribution", _ => finalCandidateSourceRoot);
        var attributionClock = Stopwatch.StartNew();
        var reading = attributionGate.RecordGateCompletion(goal, acceptance,
            new Lazy<IReadOnlyList<string>>(() => changedPaths)) ??
            throw new InvalidOperationException("Replay attribution produced no reading.");
        attributionClock.Stop();
        var completionClock = Stopwatch.StartNew();
        var result = driver.CompleteParallelLandingAcceptance(candidate, ConductorAutonomyPolicy.Conservative,
            acceptance, out var leaseHeld);
        completionClock.Stop();
        if (leaseHeld || failureDecisions != 1 || request is null)
            throw new InvalidOperationException("Replay did not reach exactly one completion disposition.");
        if (reading.SourceReadCount != request.ReadCount || reading.SourceEnumerationCount != request.EnumerationCount)
            throw new InvalidOperationException("Replay attribution and completion source counts differ.");
        var next = driver.AdvanceOnce(nextGoal, ConductorAutonomyPolicy.Conservative);
        return new(result.Outcome, failureDecisions,
            nextGoalAcceptanceRuns == 1 && next.Outcome is ConductorAdvanceOutcome.Executed,
            request.EnumerationCount, request.ReadCount, attributionClock.Elapsed.TotalMilliseconds,
            completionClock.Elapsed.TotalMilliseconds, acceptance.BranchHeadSha, acceptance.MainHeadSha);
    }
}
