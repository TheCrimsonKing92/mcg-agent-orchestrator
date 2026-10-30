using System.Xml.Linq;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Reproduction is evidence about an isolated candidate, never inferred ownership.
internal static class MergeTrainReproductionAttribution
{
    internal delegate RunResult? Runner(MergeTrainMemberBinding member, string mainRevision,
        IReadOnlyList<string> selectedTests, CancellationToken cancellationToken);

    internal sealed record RunResult(IReadOnlyList<string> ExecutedTests, IReadOnlyList<AcceptanceTrxFailure> Failures);
    internal sealed record Attribution(MergeTrainMemberBinding Member, IReadOnlyList<AcceptanceTrxFailure> Failures);

    internal static IReadOnlyList<Attribution>? TryAttribute(MergeTrainReceipt receipt,
        IReadOnlyList<MergeTrainMemberBinding> members, Runner? runner, CancellationToken cancellationToken)
    {
        if (runner is null || !MergeTrainRedAttribution.IsGenuineTrainRed(receipt)) return null;
        var fatal = MergeTrainRedAttribution.ReadFatalFailures(receipt.GateTestResultPaths);
        if (fatal.Any(MergeTrainRedAttribution.IsMessageSubjectGuard)) return null;
        var identities = fatal.Select(failure => failure.TestName ?? "").Distinct(StringComparer.Ordinal).ToArray();
        if (ConductorAcceptanceCohortFocusedAttribution.TrySelect(identities, []) is null) return null;
        var selected = identities.ToHashSet(StringComparer.Ordinal);
        var reproduced = new List<Attribution>();
        foreach (var member in members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RunResult? result;
            try { result = runner(member, receipt.Identity.ObservedMainRevision, identities, cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return null; } // Unavailable evidence preserves the ordinary split.
            if (result is null || !selected.IsSubsetOf(result.ExecutedTests) ||
                result.Failures.Any(failure => selected.Contains(failure.TestName ?? "") &&
                    (!AcceptanceTrxOutcomeTaxonomy.IsFatal(failure.Outcome) ||
                     ApparatusInfrastructureSignatures.Match(failure.Message, failure.StackTrace) is not null))) return null;
            var failures = result.Failures.Where(failure => selected.Contains(failure.TestName ?? "")).ToArray();
            if (failures.Length > 0) reproduced.Add(new(member, failures));
        }
        // Do not publish partial attribution before every member has usable results.
        return reproduced.Count == 0 ? null : reproduced;
    }

    // The parent owns goal checkpoints. Keep the member's reproduction evidence with
    // its receipt-bound ejection so reconciliation does not lose names or messages.
    internal static MergeTrainEjection CreateEjection(MergeTrainReceipt receipt, Attribution item) =>
        new(item.Member.GoalId, MergeTrainEjectionReason.RedAttributedMember, [],
            $"Dropped after RED receipt {receipt.ReceiptId}.\nreproduction=" + JsonSerializer.Serialize(
                item.Failures.Select(failure => failure with
                { Message = MergeTrainAttributedFailure.BoundMessage(failure.Message), StackTrace = null })));

    internal static IReadOnlyList<AcceptanceTrxFailure>? ReadEjectionFailures(MergeTrainReceipt receipt, string detail)
    {
        var prefix = $"Dropped after RED receipt {receipt.ReceiptId}.\nreproduction=";
        if (!detail.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var failures = JsonSerializer.Deserialize<AcceptanceTrxFailure[]>(detail[prefix.Length..]);
        var selected = MergeTrainRedAttribution.ReadFatalFailures(receipt.GateTestResultPaths)
            .Select(failure => failure.TestName).ToHashSet(StringComparer.Ordinal);
        return failures is not { Length: > 0 } || failures.Any(failure =>
            failure is null || string.IsNullOrWhiteSpace(failure.TestName) || !selected.Contains(failure.TestName) ||
            !AcceptanceTrxOutcomeTaxonomy.IsFatal(failure.Outcome) ||
            ApparatusInfrastructureSignatures.Match(failure.Message, failure.StackTrace) is not null)
            ? null : failures;
    }

    internal static Runner CreateLiveRunner(OrchestratorWorkspace workspace, IGoalAcceptanceVerifier verifier,
        GoalWorktreeCleanupHooks cleanupHooks, Func<string, CancellationToken, DotnetBuildEnvironmentLease> acquireLease) =>
        (member, mainRevision, selectedTests, cancellationToken) =>
        {
            var selection = ConductorAcceptanceCohortFocusedAttribution.TrySelect(selectedTests.ToArray(), []);
            if (selection is null) return null;
            var binding = new AcceptanceCohortMemberBinding(member.GoalId, member.BranchRevision,
                member.CandidateRevision, member.LandingPaths, member.ResourceKeys, member.ChangeRiskTier,
                member.AutoPromotionDisposition, member.MergeStatus, member.MergeReason, member.BriefVersion);
            using var partition = GoalWorktrees.CreateAcceptancePartitionWorkspace(workspace.ExecutionDirectory,
                mainRevision, binding, workspace.IntegrationBranch, cleanupHooks, requireCurrentMain: false);
            using var lease = acquireLease($"train-reproduction-{member.GoalId.Value}", cancellationToken);
            var result = AcceptanceExecutionRunner.RunFocusedVerification(verifier, partition.Path, member.GoalId,
                selection.Request, lease.Environment.BuildPermitIndex, lease, runBaselineArm: false,
                cancellationToken, integrationBranch: workspace.IntegrationBranch,
                projectHomeDirectory: workspace.ProjectHomeDirectoryOrNull);
            partition.AssertGoalBranchesUnchanged();
            var interpreted = ConductorAcceptanceCohortFocusedAttribution.Interpret(result, selection);
            if (interpreted.Kind != ConductorCohortFocusedPassKind.Executed ||
                result.OutcomeReason == FindingEvidenceOutcomeReason.CandidateInconclusive ||
                result.Arms?.Any(arm => arm.NotRunSelections?.Count > 0) == true) return null;
            return ReadResults(interpreted.TestResultPaths);
        };

    internal static RunResult? ReadResults(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return null;
        var executed = new HashSet<string>(StringComparer.Ordinal);
        var failures = new List<AcceptanceTrxFailure>();
        foreach (var path in paths)
        {
            var read = AcceptanceTrxFailureReader.Read(path);
            if (read.Status != AcceptanceTrxReadStatus.Readable) return null;
            failures.AddRange(read.Failures);
            var document = XDocument.Load(path);
            var definitions = document.Descendants().Where(element => element.Name.LocalName == "UnitTest")
                .Where(element => element.Attribute("id") is not null)
                .GroupBy(element => element.Attribute("id")!.Value, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var result in document.Descendants().Where(element => element.Name.LocalName == "UnitTestResult"))
            {
                var outcome = result.Attribute("outcome")?.Value.Trim() ?? "Unknown";
                if (!outcome.Equals("Passed", StringComparison.OrdinalIgnoreCase) &&
                    !AcceptanceTrxOutcomeTaxonomy.IsFatal(outcome)) continue;
                definitions.TryGetValue(result.Attribute("testId")?.Value ?? "", out var definition);
                if (AcceptanceTrxTestIdentityResolver.Resolve(result, definition) is { } identity) executed.Add(identity);
            }
        }
        return new(executed.ToArray(), failures);
    }
}
