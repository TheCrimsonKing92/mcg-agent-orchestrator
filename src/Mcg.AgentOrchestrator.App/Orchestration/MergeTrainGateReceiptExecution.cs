using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// The ordinary full gate shared by the initial train and focused-recheck fallback.
internal static class MergeTrainGateReceiptExecution
{
    internal static MergeTrainReceipt Run(IGoalAcceptanceVerifier verifier, string workspacePath,
        MergeTrainIdentity identity, IReadOnlyList<string> changedFiles, MergeTrainAcceptanceStore store,
        DotnetBuildEnvironmentLease stableSlotLease, AcceptanceRunExecutionOptions options,
        Stopwatch clock, Func<DateTimeOffset> utcNow, CancellationToken cancellationToken, string attemptId)
    {
        var members = identity.Members;
        var executionOwner = AcceptanceExecutionOwners.CreateAttempt(
            workspacePath,
            members[0].GoalId,
            stableSlotLease.Environment.BuildPermitIndex,
            cancellationToken,
            options);
        var verification = AcceptanceExecutionOwnerLifetime.Run(
            executionOwner,
            () => verifier.RunOwnedAsync(
                workspacePath,
                goalId: members[0].GoalId,
                changedFiles,
                stableSlotLease.Environment.BuildPermitIndex,
                stableSlotLease,
                executionOwner).GetAwaiter().GetResult());
        clock.Stop();
        var resultPaths = AcceptanceCohortVerificationClassifier.NormalizeCohortTestResultPaths(verification.TestResultPaths);
        var outcome = MergeTrainTimeoutAttribution.ResolveGateOutcome(
            AcceptanceCohortVerificationClassifier.ClassifyCohortVerificationResultWithPaths(
                verification, allowNormalized: false).Classification, resultPaths, workspacePath, members);
        var receiptId = $"merge-train-receipt-{identity.Value[(MergeTrainIdentity.Version.Length + 1)..]}";
        var receiptPaths = GroupedReceiptTestResultPaths.Apply(Console.Out, "train", receiptId, verification, resultPaths);
        var failedChecks = verification.Checks?.Where(check => !check.Passed && !check.Advisory).ToArray() ?? [];
        return store.SaveGateReceipt(new MergeTrainReceipt(
            receiptId,
            identity,
            outcome,
            utcNow(),
            checked((long)clock.Elapsed.TotalMilliseconds),
            failedChecks.Select(check => check.Name).ToArray(),
            verification.ExitCode,
            receiptPaths,
            ValidForLanding: outcome == MergeTrainGateOutcome.Passed)
        {
            GateAttemptId = attemptId,
            FailedCheckEvidence = failedChecks.Select(check => ReadFailedCheckEvidence(check, receiptPaths)).ToArray()
        });
    }

    private static MergeTrainFailedCheckEvidence ReadFailedCheckEvidence(
        AcceptanceCheckResult check, IReadOnlyList<string> receiptPaths)
    {
        IReadOnlyList<string> paths;
        try { paths = AcceptanceCohortVerificationClassifier.NormalizeCohortTestResultPaths(check.TestResultPaths); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return new(check.Name, false, []); }
        // Honor the same within-attempt TRX substitution as the retained receipt.
        paths = GroupedReceiptTestResultPaths.Resolve(
            new(false, false, check.ExitCode, null, Checks: [check]), paths).Paths;
        var reads = paths.Select(AcceptanceTrxFailureReader.Read).ToArray();
        var fatal = reads.SelectMany(read => read.Failures)
            .Where(failure => AcceptanceTrxOutcomeTaxonomy.IsFatal(failure.Outcome)).ToArray();
        var identities = fatal.Where(failure => !string.IsNullOrWhiteSpace(failure.TestName))
            .Select(failure => AcceptanceTrxTestIdentityResolver.NormalizeSelector(failure.TestName!))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var explained = reads.Length > 0 && reads.All(read => read.Status == AcceptanceTrxReadStatus.Readable) &&
            paths.All(path => receiptPaths.Contains(path, StringComparer.OrdinalIgnoreCase)) &&
            fatal.Length > 0 && fatal.All(failure => !string.IsNullOrWhiteSpace(failure.TestName));
        return new(check.Name, explained, identities);
    }

    internal static bool HasExplainedFailedChecks(MergeTrainReceipt red, IReadOnlyList<string> failing)
    {
        var evidence = red.FailedCheckEvidence;
        if (evidence is null || red.FailedChecks.Count == 0 ||
            evidence.Any(check => check is null || check.FatalTestIdentities is null) ||
            !evidence.Select(check => check.CheckName).SequenceEqual(red.FailedChecks, StringComparer.Ordinal) ||
            evidence.Any(check => !check.ExplainedByFatalTrx || check.FatalTestIdentities.Count == 0 ||
                check.FatalTestIdentities.Any(identity => !failing.Contains(identity, StringComparer.Ordinal)))) return false;
        // Bind this new authorization input to the retained gate verdict, rather
        // than accepting coverage added to a legacy receipt after the gate ran.
        try
        {
            var verdict = red.GateEvidenceArtifacts.Single(artifact => artifact.Kind == "gate-verdict");
            if (!AcceptanceCohortGateEvidence.HasContentBoundEvidence(red.GateTestResultPaths, red.GateEvidenceArtifacts)) return false;
            using var document = JsonDocument.Parse(File.ReadAllBytes(verdict.Path));
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            return JsonSerializer.Serialize(document.RootElement.GetProperty("failedChecks"), options) ==
                JsonSerializer.Serialize(red.FailedChecks, options) &&
                JsonSerializer.Serialize(document.RootElement.GetProperty("failedCheckEvidence"), options) ==
                JsonSerializer.Serialize(evidence, options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
            InvalidOperationException or KeyNotFoundException) { return false; }
    }
}
