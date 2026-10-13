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
    internal const string ModeVariable = "MCG_ACCEPTANCE_ATTRIBUTION_REPLAY_MODE";
    internal const string RequestVariable = "MCG_ACCEPTANCE_ATTRIBUTION_REPLAY_REQUEST";
    internal sealed record CopiedFile(string File, string Sha256);
    internal sealed record ReplayRequest(int SchemaVersion, string RequestId, string ReplayKind,
        string InputPath, string InputSha256, string SourceRoot, string FinalCandidateSha,
        string AssemblySha256, CopiedFile[] Sources, string ResultPath);
    internal sealed record CopiedAttemptInput(string SourceRoot, string FinalCandidateSha,
        CopiedFile Attempt, CopiedFile Result, CopiedFile OriginalTrx, CopiedFile RerunTrx,
        CopiedFile OriginalHeartbeat, CopiedFile RerunHeartbeat);

    internal sealed record Receipt(ConductorAdvanceOutcome Outcome, int FailureDecisions,
        bool NextGoalAdvanced, int SourceEnumerations, int SourceReads,
        double AttributionMilliseconds, double CompletionMilliseconds, string? CandidateSha, string? MainSha);

    internal static ReplayRequest ValidateRequest(string? mode, string? requestJson)
    {
        if (mode != "actual-copy" || string.IsNullOrWhiteSpace(requestJson))
            throw new InvalidOperationException("Actual-copy replay requires the explicit managed helper request.");
        var request = JsonSerializer.Deserialize<ReplayRequest>(requestJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ??
            throw new InvalidOperationException("Replay request is missing.");
        if (request.SchemaVersion != 1 || request.ReplayKind != "actual-copy" ||
            !Guid.TryParseExact(request.RequestId, "N", out _) || !Path.IsPathFullyQualified(request.InputPath) ||
            !Path.IsPathFullyQualified(request.SourceRoot) || !Path.IsPathFullyQualified(request.ResultPath) ||
            request.FinalCandidateSha?.Length != 40 || request.Sources is not { Length: > 0 })
            throw new InvalidOperationException("Replay request identity is invalid.");
        ValidateHash(typeof(AcceptanceAttributionReplayFixture).Assembly.Location, request.AssemblySha256);
        foreach (var source in request.Sources)
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.SourceRoot));
            var path = Path.GetFullPath(Path.Combine(root, source.File));
            if (Path.IsPathRooted(source.File) || !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Replay source escapes the final candidate.");
            ValidateHash(path, source.Sha256);
        }
        return request;
    }

    private static void ValidateHash(string path, string expected)
    {
        using var stream = File.OpenRead(path);
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Replay source/assembly hash mismatch.");
    }

    internal static Receipt ReplayCopiedAttempt(string mode, string requestJson)
    {
        var request = ValidateRequest(mode, requestJson);
        var inputPath = request.InputPath;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        if (new FileInfo(inputPath).Length > 1048576) throw new InvalidOperationException("Replay input exceeds its bound.");
        var inputBytes = File.ReadAllBytes(inputPath);
        if (!Convert.ToHexString(SHA256.HashData(inputBytes)).Equals(request.InputSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Replay input hash mismatch.");
        var input = JsonSerializer.Deserialize<CopiedAttemptInput>(inputBytes, options) ??
            throw new InvalidOperationException("Replay input is missing.");
        if (input.FinalCandidateSha != request.FinalCandidateSha ||
            !Path.TrimEndingDirectorySeparator(Path.GetFullPath(input.SourceRoot)).Equals(request.SourceRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Replay input final source/candidate binding mismatch.");
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
        if (receipt.FailureDecisions != 1 || !receipt.NextGoalAdvanced || receipt.SourceEnumerations != 0 || receipt.SourceReads != 0 ||
            receipt.Outcome is not ConductorAdvanceOutcome.Held)
            throw new InvalidOperationException("Replay completion did not retain its failed decision and bounded attribution.");
        // A passing test's console output is not retained. Publish a fresh, bounded artifact that
        // the helper validates and copies beside the retained TRX before clean-run cleanup.
        using var report = new FileStream(request.ResultPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var requestDocument = JsonDocument.Parse(requestJson);
        JsonSerializer.Serialize(report, new { replayKind = "actual-copy", request = requestDocument.RootElement,
            copiedReceipts = input, historicalAttemptId = attempt.AttemptId, originalTrxRunId = originalRun,
            rerunTrxRunId = rerunRun, receipt }, options);
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
