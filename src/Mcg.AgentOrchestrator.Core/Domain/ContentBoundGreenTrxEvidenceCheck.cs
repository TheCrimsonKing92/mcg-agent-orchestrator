using System.Security.Cryptography;
using System.Xml.Linq;

namespace Mcg.AgentOrchestrator.Core;

public sealed record ContentBoundGreenTrxEvidenceFailure(string Check, IReadOnlyList<string> MissingClasses);

public static class ContentBoundGreenTrxEvidenceCheck
{
    public static ContentBoundGreenTrxEvidenceFailure? FirstFailure(
        IReadOnlyList<string>? testResultPaths,
        IReadOnlyList<AcceptanceCohortEvidenceArtifact>? artifacts,
        int expectedExecutedTestCount,
        IReadOnlyCollection<string> requiredTestClasses,
        bool allowShortNamePrefixCoverage = false)
    {
        if (!AcceptanceCohortGateEvidence.HasNormalizedTestResultPaths(testResultPaths))
            return new("trx-paths-invalid", []);
        if (artifacts is not { Count: > 0 }) return new("trx-artifacts-missing", []);
        if (expectedExecutedTestCount <= 0) return new("executed-count-missing", []);
        if (requiredTestClasses.Count == 0) return new("no-required-classes", []);

        var trxArtifacts = artifacts
            .Where(artifact => artifact.Kind.Equals("trx", StringComparison.Ordinal))
            .OrderBy(artifact => artifact.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var normalizedTrxPaths = testResultPaths!
            .Select(Path.GetFullPath)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (trxArtifacts.Length != normalizedTrxPaths.Length ||
            !trxArtifacts.Select(artifact => artifact.Path)
                .SequenceEqual(normalizedTrxPaths, StringComparer.OrdinalIgnoreCase))
            return new("trx-path-set-mismatch", []);

        var executedCount = 0;
        var executedClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in trxArtifacts)
        {
            if (!TryReadContentBoundGreenTrx(artifact, out var document, out var artifactExecutedCount))
                return new("trx-content-not-green", []);
            if (executedCount > int.MaxValue - artifactExecutedCount)
                return new("executed-count-mismatch", []);
            executedCount += artifactExecutedCount;
            if (!TryAddExecutedTestClasses(document, executedClasses))
                return new("trx-duplicate-test-id", []);
        }

        if (executedCount != expectedExecutedTestCount) return new("executed-count-mismatch", []);
        var missingClasses = requiredTestClasses.Where(required => !executedClasses.Contains(required) &&
                !(allowShortNamePrefixCoverage && executedClasses.Any(executed =>
                    IsShortNamePrefixMatch(executed, required))))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        return missingClasses.Length == 0 ? null : new("required-class-missing", missingClasses);
    }

    private static bool IsShortNamePrefixMatch(string executed, string required)
    {
        var requiredShortName = required[(required.LastIndexOf('.') + 1)..];
        var executedShortName = executed[(executed.LastIndexOf('.') + 1)..];
        return requiredShortName.Length > 0 &&
            executedShortName.StartsWith(requiredShortName, StringComparison.Ordinal);
    }

    private static bool TryReadContentBoundGreenTrx(
        AcceptanceCohortEvidenceArtifact artifact,
        out XDocument document,
        out int executedTestCount)
    {
        document = null!;
        executedTestCount = 0;
        if (string.IsNullOrWhiteSpace(artifact.Path) ||
            !Path.IsPathFullyQualified(artifact.Path) ||
            artifact.Length <= 0 ||
            artifact.Sha256.Length != 64 ||
            artifact.Sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            return false;
        }

        try
        {
            using var stream = File.Open(artifact.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length != artifact.Length)
            {
                return false;
            }

            var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (!hash.Equals(artifact.Sha256, StringComparison.Ordinal))
            {
                return false;
            }

            stream.Position = 0;
            document = XDocument.Load(stream, LoadOptions.None);
            if (!AcceptanceCohortGateEvidence.HasCoherentExecutedTrxEvidence(document))
            {
                return false;
            }

            XNamespace trxNamespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            var counters = document.Root?
                .Element(trxNamespace + "ResultSummary")?
                .Element(trxNamespace + "Counters");
            return counters is not null &&
                AcceptanceCohortGateEvidence.TryReadNonNegativeCounter(counters, "executed", out executedTestCount) &&
                AcceptanceCohortGateEvidence.TryReadNonNegativeCounter(counters, "failed", out var failedTestCount) &&
                failedTestCount == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or OverflowException)
        {
            return false;
        }
    }

    private static bool TryAddExecutedTestClasses(XDocument document, ISet<string> executedClasses)
    {
        XNamespace trxNamespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var definitionRows = document.Root?
            .Element(trxNamespace + "TestDefinitions")?
            .Elements(trxNamespace + "UnitTest")
            .Select(test => new
            {
                Id = (string?)test.Attribute("id"),
                ClassName = (string?)test.Element(trxNamespace + "TestMethod")?.Attribute("className")
            })
            .Where(definition => !string.IsNullOrWhiteSpace(definition.Id) && !string.IsNullOrWhiteSpace(definition.ClassName))
            .ToArray()
            ?? [];
        var definitions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitionRows)
        {
            if (!definitions.TryAdd(definition.Id!, definition.ClassName!))
            {
                return false;
            }
        }

        var executedIds = document.Root?
            .Element(trxNamespace + "Results")?
            .Elements(trxNamespace + "UnitTestResult")
            .Select(result => (string?)result.Attribute("testId"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? [];
        foreach (var definition in definitions.Where(definition => executedIds.Contains(definition.Key)))
        {
            executedClasses.Add(definition.Value);
            executedClasses.Add(definition.Value.Split('.').Last());
        }

        return true;
    }

}
