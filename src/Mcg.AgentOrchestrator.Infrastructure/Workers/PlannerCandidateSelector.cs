using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record PlannerCandidateInput(int Index, string StandardOutput, string StandardError = "");

internal sealed record PlannerCandidateSelectionResult(
    PlannerOutputContractResult SelectedContract,
    PlannerCandidateDivergenceReceipt Receipt);

internal static partial class PlannerCandidateSelector
{
    internal const string SelectionSignal = "orchestrator-peer-agreement-jaccard-v1";
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

        var resolved = candidates
            .Select(candidate => new ResolvedCandidate(
                candidate.Index,
                PlannerOutputContract.Resolve(
                    candidate.StandardOutput,
                    candidate.StandardError,
                    workingDirectory,
                    acceptanceCriteria: acceptanceCriteria)))
            .OrderBy(candidate => candidate.Index)
            .ToArray();
        var valid = resolved.Where(candidate => candidate.Contract.Succeeded && candidate.Contract.Plan is not null).ToArray();
        var primary = resolved.FirstOrDefault(candidate => candidate.Index == 0) ?? resolved[0];

        if (valid.Length < 2)
        {
            return new PlannerCandidateSelectionResult(
                primary.Contract,
                BuildReceipt(resolved, valid, primary.Index, new double[resolved.Length]));
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

        var selected = valid
            .OrderByDescending(candidate => scores[candidate.Index])
            .ThenBy(candidate => candidate.Index)
            .First();
        return new PlannerCandidateSelectionResult(
            selected.Contract,
            BuildReceipt(resolved, valid, selected.Index, scores, candidateFeatures));
    }

    private static PlannerCandidateDivergenceReceipt BuildReceipt(
        IReadOnlyList<ResolvedCandidate> resolved,
        IReadOnlyList<ResolvedCandidate> valid,
        int selectedIndex,
        IReadOnlyList<double> scores,
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
                .ToArray());
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

    private sealed record ResolvedCandidate(int Index, PlannerOutputContractResult Contract);

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
