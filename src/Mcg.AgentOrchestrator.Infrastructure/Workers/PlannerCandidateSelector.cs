using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record PlannerCandidateInput(
    int Index,
    string StandardOutput,
    string StandardError = "",
    string? SourcePath = null,
    string? ArtifactSha256 = null,
    PlannerCandidateTerminalState TerminalState = PlannerCandidateTerminalState.Succeeded,
    PlannerCandidateNormalizationState NormalizationState = PlannerCandidateNormalizationState.NotRequired,
    long? ElapsedMilliseconds = null,
    ProviderReportedUsage? ProviderUsage = null,
    string ProviderUsageUnavailableReason = "unsupported");

internal sealed record PlannerCandidateSelectionResult(
    PlannerOutputContractResult SelectedContract,
    PlannerCandidateDivergenceReceipt Receipt);

internal static partial class PlannerCandidateSelector
{
    internal const string SelectionSignal = "orchestrator-planner-candidate-selection-v2";
    private const int MaximumFeaturesPerSection = 32;
    private const int MaximumFeatureLength = 200;

    internal static PlannerCandidateSelectionResult Select(
        IReadOnlyList<PlannerCandidateInput> candidates,
        string workingDirectory,
        IReadOnlyList<string>? acceptanceCriteria = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
            throw new ArgumentException("At least one Planner candidate is required.", nameof(candidates));
        if (!candidates.Select(candidate => candidate.Index).Order().SequenceEqual(Enumerable.Range(0, candidates.Count)))
            throw new ArgumentException("Planner candidate indexes must be unique, contiguous, and zero-based.", nameof(candidates));

        var resolved = candidates.Select(candidate =>
        {
            var eligible = IsEligibleForContract(candidate);
            var contract = eligible
                ? PlannerOutputContract.Resolve(
                    candidate.StandardOutput,
                    candidate.StandardError,
                    workingDirectory,
                    acceptanceCriteria: acceptanceCriteria)
                : new PlannerOutputContractResult(
                    false,
                    null,
                    null,
                    BuildIneligibleDiagnostic(candidate));
            var quality = contract.Succeeded && contract.Plan is not null
                ? PlannerOutputContract.EvaluateStructuralQuality(contract.Plan)
                : null;
            return new ResolvedCandidate(candidate, contract, quality);
        }).OrderBy(candidate => candidate.Index).ToArray();
        var valid = resolved.Where(candidate => candidate.Contract.Succeeded && candidate.Contract.Plan is not null).ToArray();
        var primary = resolved.FirstOrDefault(candidate => candidate.Index == 0) ?? resolved[0];

        if (valid.Length < 2)
        {
            var selectedIndex = primary.Contract.Succeeded ? primary.Index : (int?)null;
            var insufficientFallbackCause = selectedIndex is null
                ? "fewer-than-two-comparable-candidates-and-primary-invalid"
                : "fewer-than-two-comparable-candidates";
            return new PlannerCandidateSelectionResult(
                primary.Contract,
                BuildReceipt(
                    resolved,
                    valid,
                    selectedIndex,
                    new double[resolved.Length],
                    selectedIndex is null ? "no-selection" : "primary-fallback",
                    insufficientFallbackCause));
        }

        var candidateFeatures = valid.ToDictionary(
            candidate => candidate.Index,
            candidate => ExtractFeatures(candidate.Contract.Plan!));
        var scores = new double[resolved.Length];
        foreach (var candidate in valid)
        {
            var peers = valid.Where(peer => peer.Index != candidate.Index).ToArray();
            scores[candidate.Index] = peers.Average(peer => Jaccard(
                candidateFeatures[candidate.Index].All,
                candidateFeatures[peer.Index].All));
        }

        ResolvedCandidate selected;
        string selectionReason;
        string? fallbackCause = null;
        if (valid.Length == 2)
        {
            if (string.Equals(valid[0].CandidateSha256, valid[1].CandidateSha256, StringComparison.Ordinal))
            {
                selected = primary.Contract.Succeeded ? primary : valid[0];
                selectionReason = "identical-candidates";
                fallbackCause = "normalized-candidate-hashes-identical";
            }
            else
            {
                var comparison = CompareStructuralQuality(valid[0].StructuralQuality!, valid[1].StructuralQuality!);
                if (comparison == 0)
                {
                    selected = primary.Contract.Succeeded ? primary : valid[0];
                    selectionReason = "primary-fallback";
                    fallbackCause = "structural-quality-tie";
                }
                else
                {
                    selected = comparison > 0 ? valid[0] : valid[1];
                    selectionReason = "structural-quality";
                }
            }
        }
        else
        {
            selected = valid
                .OrderByDescending(candidate => scores[candidate.Index])
                .ThenBy(candidate => candidate.Index)
                .First();
            selectionReason = "peer-agreement";
        }

        var selectedContract = selected.Contract.IngestedPath is null && selected.SourcePath is not null
            ? selected.Contract with { IngestedPath = selected.SourcePath }
            : selected.Contract;
        return new PlannerCandidateSelectionResult(
            selectedContract,
            BuildReceipt(
                resolved,
                valid,
                selected.Index,
                scores,
                selectionReason,
                fallbackCause,
                candidateFeatures));
    }

    private static PlannerCandidateDivergenceReceipt BuildReceipt(
        IReadOnlyList<ResolvedCandidate> resolved,
        IReadOnlyList<ResolvedCandidate> valid,
        int? selectedIndex,
        IReadOnlyList<double> scores,
        string selectionReason,
        string? fallbackCause,
        IReadOnlyDictionary<int, CandidateFeatures>? featureMap = null)
    {
        featureMap ??= valid.ToDictionary(
            candidate => candidate.Index,
            candidate => ExtractFeatures(candidate.Contract.Plan!));
        var sectionNames = featureMap.Values.SelectMany(features => features.BySection.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(section => section, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var sections = new List<PlannerSectionDivergence>();
        foreach (var section in sectionNames)
        {
            var sectionSets = valid.Select(candidate => featureMap[candidate.Index].BySection.GetValueOrDefault(section, [])).ToArray();
            var agreed = sectionSets.Length == 0
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(sectionSets[0], StringComparer.Ordinal);
            foreach (var features in sectionSets.Skip(1))
                agreed.IntersectWith(features);

            var candidateSections = valid.Select(candidate =>
            {
                var features = featureMap[candidate.Index].BySection.GetValueOrDefault(section, []);
                var body = featureMap[candidate.Index].Bodies.GetValueOrDefault(section, string.Empty);
                return new PlannerCandidateSectionFeatures(
                    candidate.Index,
                    Hash(body),
                    Bound(features.Except(agreed, StringComparer.Ordinal)));
            }).ToArray();
            if (candidateSections.Any(candidate => candidate.DivergentFeatures.Count > 0))
                sections.Add(new PlannerSectionDivergence(section, Bound(agreed), candidateSections));
        }

        return new PlannerCandidateDivergenceReceipt(
            resolved.Count,
            selectedIndex,
            SelectionSignal,
            Enumerable.Range(0, resolved.Count).Select(index => index < scores.Count ? scores[index] : 0).ToArray(),
            sections,
            resolved.Where(candidate => !candidate.Contract.Succeeded)
                .Select(candidate => $"candidate {candidate.Index}: {BoundText(candidate.Contract.Diagnostic)}")
                .ToArray(),
            selectionReason,
            fallbackCause,
            resolved.Select(BuildCandidateEvidence).ToArray());
    }

    private static CandidateFeatures ExtractFeatures(string plan)
    {
        var bodies = PlannerOutputContract.SplitRequiredSections(plan);
        var bySection = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (section, body) in bodies)
        {
            var features = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in CodeSpan().Matches(body))
                features.Add("code:" + Normalize(match.Groups[1].Value));
            foreach (Match match in Word().Matches(body))
            {
                var word = match.Value.ToLowerInvariant();
                if (word.Length >= 4)
                    features.Add("word:" + word);
            }
            bySection[section] = features;
        }

        return new CandidateFeatures(
            bodies,
            bySection,
            bySection.SelectMany(section => section.Value.Select(feature => section.Key + ":" + feature))
                .ToHashSet(StringComparer.Ordinal));
    }

    private static double Jaccard(IReadOnlySet<string> left, IReadOnlySet<string> right)
    {
        var union = left.Union(right, StringComparer.Ordinal).Count();
        return union == 0 ? 0 : (double)left.Intersect(right, StringComparer.Ordinal).Count() / union;
    }

    private static bool IsEligibleForContract(PlannerCandidateInput candidate) =>
        candidate.TerminalState == PlannerCandidateTerminalState.Succeeded &&
        candidate.NormalizationState is PlannerCandidateNormalizationState.NotRequired or
            PlannerCandidateNormalizationState.Normalized &&
        !string.IsNullOrWhiteSpace(candidate.StandardOutput);

    private static string BuildIneligibleDiagnostic(PlannerCandidateInput candidate) =>
        candidate.TerminalState != PlannerCandidateTerminalState.Succeeded
            ? $"Planner candidate terminal state was {candidate.TerminalState}."
            : $"Planner candidate normalization state was {candidate.NormalizationState}.";

    private static int CompareStructuralQuality(
        PlannerStructuralQualityVector left,
        PlannerStructuralQualityVector right)
    {
        int[] leftComponents =
        [
            left.CompleteMappings,
            left.ConcreteOwningSeams,
            left.FeasibleEvidenceOwners,
            left.IntegrationSeams,
            left.VerificationClasses,
            left.StopConditions
        ];
        int[] rightComponents =
        [
            right.CompleteMappings,
            right.ConcreteOwningSeams,
            right.FeasibleEvidenceOwners,
            right.IntegrationSeams,
            right.VerificationClasses,
            right.StopConditions
        ];
        for (var index = 0; index < leftComponents.Length; index++)
        {
            var comparison = leftComponents[index].CompareTo(rightComponents[index]);
            if (comparison != 0)
                return comparison;
        }

        return 0;
    }

    private static PlannerCandidateEvidenceReceipt BuildCandidateEvidence(ResolvedCandidate candidate)
    {
        var input = candidate.Input;
        var usage = input.ProviderUsage;
        var usageReported = usage?.InputTokens is not null ||
                            usage?.CachedInputTokens is not null ||
                            usage?.OutputTokens is not null;
        var verdict = !IsEligibleForContract(input)
            ? PlannerCandidateContractVerdict.NotEvaluated
            : candidate.Contract.Succeeded
                ? PlannerCandidateContractVerdict.Valid
                : PlannerCandidateContractVerdict.Invalid;
        return new PlannerCandidateEvidenceReceipt(
            candidate.Index,
            candidate.CandidateSha256,
            input.ArtifactSha256 ?? Hash(input.StandardOutput),
            input.TerminalState,
            input.NormalizationState,
            verdict,
            input.ElapsedMilliseconds,
            new PlannerCandidateUsageReceipt(
                usageReported ? PlannerCandidateUsageState.Reported : PlannerCandidateUsageState.Unknown,
                usage?.InputTokens,
                usage?.CachedInputTokens,
                usage?.OutputTokens,
                usageReported ? null : input.ProviderUsageUnavailableReason),
            candidate.StructuralQuality,
            BoundText(candidate.Contract.Diagnostic));
    }

    private static IReadOnlyList<string> Bound(IEnumerable<string> features) => features
        .Select(BoundText)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(feature => feature, StringComparer.Ordinal)
        .Take(MaximumFeaturesPerSection)
        .ToArray();

    private static string BoundText(string value) =>
        value.Length <= MaximumFeatureLength ? value : value[..MaximumFeatureLength];

    private static string Normalize(string value) =>
        Whitespace().Replace(value.Trim().ToLowerInvariant(), " ");

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ReplaceLineEndings("\n")))).ToLowerInvariant();

    private sealed record ResolvedCandidate(
        PlannerCandidateInput Input,
        PlannerOutputContractResult Contract,
        PlannerStructuralQualityVector? StructuralQuality)
    {
        internal int Index => Input.Index;
        internal string? SourcePath => Input.SourcePath;
        internal string? CandidateSha256 => Contract.Plan is null ? null : Hash(Contract.Plan);
    }

    private sealed record CandidateFeatures(
        IReadOnlyDictionary<string, string> Bodies,
        IReadOnlyDictionary<string, HashSet<string>> BySection,
        IReadOnlySet<string> All);

    [GeneratedRegex(@"`([^`\r\n]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex CodeSpan();

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}_./\\:-]*", RegexOptions.CultureInvariant)]
    private static partial Regex Word();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
