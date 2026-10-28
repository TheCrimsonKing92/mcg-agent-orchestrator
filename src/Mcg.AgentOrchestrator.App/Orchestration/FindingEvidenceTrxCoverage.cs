using System.Security.Cryptography;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class FindingEvidenceTrxCoverage
{
    internal static bool HasPassedSelections(FindingEvidenceArmReceipt arm, FindingEvidenceRequest request) =>
        HasExecutedSelections(arm, request, requirePassed: true);

    internal static bool HasRedSelections(FindingEvidenceArmReceipt arm, FindingEvidenceRequest request) =>
        HasExecutedSelections(arm, request, requirePassed: false);

    private static bool HasExecutedSelections(FindingEvidenceArmReceipt arm, FindingEvidenceRequest request, bool requirePassed)
    {
        var selectors = FindingEvidenceRoundPlanner.ExpandSelections(request.Selections);
        if (selectors.Count == 0 || arm.TestResultPaths is not { Count: > 0 } paths) return false;
        try
        {
            var identities = new HashSet<string>(StringComparer.Ordinal);
            var classes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in paths)
            {
                var document = XDocument.Load(path);
                var definitions = document.Descendants().Where(item => item.Name.LocalName == "UnitTest")
                    .ToDictionary(item => (string?)item.Attribute("id") ?? "", StringComparer.Ordinal);
                foreach (var result in document.Descendants().Where(item => item.Name.LocalName == "UnitTestResult"))
                {
                    definitions.TryGetValue((string?)result.Attribute("testId") ?? "", out var definition);
                    var identity = AcceptanceTrxTestIdentityResolver.Resolve(result, definition);
                    var testClass = definition?.Descendants().FirstOrDefault(item => item.Name.LocalName == "TestMethod")
                        ?.Attribute("className")?.Value;
                    if (identity is not null) identities.Add(identity);
                    if (testClass is not null) classes.Add(testClass);
                }
            }
            if (requirePassed
                ? !AcceptanceCohortGateEvidence.HasContentBoundGreenTrxEvidence(paths, arm.ReceiptArtifacts,
                    arm.ExecutedTestCount ?? 0, classes.ToArray())
                : !HasContentBoundRedTrxCoverage(arm, classes.ToArray())) return false;
            foreach (var selection in selectors)
            {
                if (!FindingEvidenceFilterSyntax.TryGetExpandableClass(selection.TestClass, out var token, out _))
                    return false; // Unknown coverage never grants continuation authority.
                var matching = identities.Where(id => id == token || id.StartsWith(token + ".", StringComparison.Ordinal) ||
                    id.EndsWith("." + token, StringComparison.Ordinal) || id.Contains("." + token + ".", StringComparison.Ordinal)).ToArray();
                if (matching.Length == 0 || !HasExecutedIdentities(arm, matching, requirePassed)) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or ArgumentException)
        {
            return false;
        }
    }

    // The arm's content hashes, rather than a class-level green summary, bind this proof.
    internal static bool HasPassedIdentities(FindingEvidenceArmReceipt arm, IReadOnlyList<string> identities) =>
        HasExecutedIdentities(arm, identities, requirePassed: true);

    private static bool HasExecutedIdentities(FindingEvidenceArmReceipt arm, IReadOnlyList<string> identities, bool requirePassed)
    {
        if (identities.Count == 0 || arm.ExecutedTestCount is not > 0 ||
            arm.TestResultPaths is not { Count: > 0 } paths ||
            arm.ReceiptArtifacts is not { Count: > 0 } artifacts ||
            !AcceptanceCohortGateEvidence.HasCoherentTrxEvidence(paths)) return false;
        try
        {
            var trxArtifacts = artifacts.Where(item => item.Kind == "trx").ToArray();
            if (trxArtifacts.Length != paths.Count ||
                !trxArtifacts.Select(item => item.Path).Order(StringComparer.OrdinalIgnoreCase)
                    .SequenceEqual(paths.Select(Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase),
                        StringComparer.OrdinalIgnoreCase)) return false;
            var outcomes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var executed = 0;
            foreach (var artifact in trxArtifacts)
            {
                using var stream = File.Open(artifact.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (artifact.Length <= 0 || stream.Length != artifact.Length ||
                    !Convert.ToHexStringLower(SHA256.HashData(stream)).Equals(artifact.Sha256, StringComparison.Ordinal))
                    return false;
                stream.Position = 0;
                var document = XDocument.Load(stream);
                var definitions = document.Descendants().Where(item => item.Name.LocalName == "UnitTest")
                    .ToDictionary(item => (string?)item.Attribute("id") ?? "", StringComparer.Ordinal);
                foreach (var result in document.Descendants().Where(item => item.Name.LocalName == "UnitTestResult"))
                {
                    definitions.TryGetValue((string?)result.Attribute("testId") ?? "", out var definition);
                    var identity = AcceptanceTrxTestIdentityResolver.Resolve(result, definition);
                    var outcome = (string?)result.Attribute("outcome") ?? "";
                    if (outcome is "Passed" or "Failed") executed++;
                    if (identity is null) continue;
                    if (!outcomes.TryGetValue(identity, out var values)) outcomes[identity] = values = [];
                    values.Add(outcome);
                }
            }
            return executed == arm.ExecutedTestCount && identities.All(identity =>
                outcomes.TryGetValue(identity, out var values) && values.Count > 0 &&
                values.All(value => value == "Passed" || !requirePassed && value == "Failed"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or
                                   ArgumentException or NotSupportedException or OverflowException)
        {
            return false;
        }
    }

    internal static bool HasContentBoundRedTrxCoverage(
        FindingEvidenceArmReceipt candidateArm,
        IReadOnlyCollection<string> requiredTestClasses)
    {
        if (candidateArm is not { ExecutedTestCount: > 0 } ||
            candidateArm.TestResultPaths is not { Count: > 0 } testResultPaths ||
            candidateArm.ReceiptArtifacts is not { Count: > 0 } receiptArtifacts ||
            requiredTestClasses.Count == 0 ||
            !AcceptanceCohortGateEvidence.HasCoherentTrxEvidence(testResultPaths))
        {
            return false;
        }

        try
        {
            var normalizedPaths = testResultPaths
                .Select(Path.GetFullPath)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var trxArtifacts = receiptArtifacts
                .Where(artifact => artifact.Kind.Equals("trx", StringComparison.Ordinal))
                .OrderBy(artifact => artifact.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (trxArtifacts.Length != normalizedPaths.Length ||
                !trxArtifacts.Select(artifact => artifact.Path)
                    .SequenceEqual(normalizedPaths, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            var executedCount = 0;
            var failedCount = 0;
            var executedClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            XNamespace trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            foreach (var artifact in trxArtifacts)
            {
                using var stream = File.Open(artifact.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length != artifact.Length || artifact.Length <= 0)
                {
                    return false;
                }

                var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
                if (!hash.Equals(artifact.Sha256, StringComparison.Ordinal))
                {
                    return false;
                }

                stream.Position = 0;
                var document = XDocument.Load(stream, LoadOptions.None);
                var definitions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var test in document.Root?
                             .Element(trx + "TestDefinitions")?
                             .Elements(trx + "UnitTest") ?? [])
                {
                    var id = (string?)test.Attribute("id");
                    var className = (string?)test.Element(trx + "TestMethod")?.Attribute("className");
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(className) ||
                        !definitions.TryAdd(id, className))
                    {
                        return false;
                    }
                }

                foreach (var result in document.Root?
                             .Element(trx + "Results")?
                             .Elements(trx + "UnitTestResult") ?? [])
                {
                    var outcome = (string?)result.Attribute("outcome");
                    if (!string.Equals(outcome, "Passed", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(outcome, "Failed", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var id = (string?)result.Attribute("testId");
                    if (string.IsNullOrWhiteSpace(id) || !definitions.TryGetValue(id, out var className))
                    {
                        return false;
                    }

                    executedCount++;
                    if (string.Equals(outcome, "Failed", StringComparison.OrdinalIgnoreCase))
                    {
                        failedCount++;
                    }
                    executedClasses.Add(className);
                    executedClasses.Add(className.Split('.').Last());
                }
            }

            return failedCount > 0 &&
                executedCount == candidateArm.ExecutedTestCount &&
                requiredTestClasses.All(required => executedClasses.Contains(required));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   System.Xml.XmlException or ArgumentException or NotSupportedException or
                                   PathTooLongException or OverflowException)
        {
            return false;
        }
    }

}
