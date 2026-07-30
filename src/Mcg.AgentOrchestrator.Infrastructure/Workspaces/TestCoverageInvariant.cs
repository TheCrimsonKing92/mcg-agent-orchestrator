using System.Xml.Linq;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record TestPartitionCoverage(
    string Name,
    bool Completed,
    IReadOnlyList<string> TestResultPaths,
    bool HasEnvironmentInterferenceEvidence = false,
    string? AttemptId = null,
    int RunOrdinal = 0,
    bool IsExplicitCrossAttemptReuse = false);

internal sealed record TestCoverageInvariantResult(
    bool Passed,
    string Summary,
    IReadOnlyList<string> MissingTests,
    IReadOnlyList<string> EmptyPartitions,
    string? FailureClassification,
    IReadOnlyList<string> ExecutedTests);

internal static class TestCoverageInvariant
{
    private static readonly string[] BareTestListDiagnosticPrefixes =
    [
        "Microsoft.Testing.Platform",
        "Test discovery summary:",
        "Test run summary",
        "Test run for",
        "Test execution",
        "Total tests",
        "Tests found",
        "Test modules",
        "Passed:",
        "Failed:",
        "Skipped:",
        "Succeeded:",
        "Total:",
        "Duration:",
        "Artifacts produced:",
        "Build ",
        "Starting test",
        "Discovering",
        "Executing tests",
        "Running tests",
        "Results File:",
        "Attachments:",
        "No test",
        "Skipping real-worker process guard:",
        "Warning",
        "Error"
    ];

    public static IReadOnlySet<string> ParseDiscoveredTests(string output, bool bareTestList = false)
    {
        var tests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inTestList = false;
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Contains("tests are available", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("available tests:", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("the following tests are available:", StringComparison.OrdinalIgnoreCase))
            {
                inTestList = true;
                continue;
            }

            if (line.StartsWith("DISCOVERED_TEST:", StringComparison.OrdinalIgnoreCase))
            {
                AddNormalized(tests, line["DISCOVERED_TEST:".Length..]);
                continue;
            }

            if (bareTestList)
            {
                if (!IsBareTestListDiagnostic(line))
                {
                    AddNormalized(tests, line);
                }

                continue;
            }

            if (!inTestList ||
                line.StartsWith("test run", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("total tests", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("build ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("passed", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("failed", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            AddNormalized(tests, line);
        }

        return tests;
    }

    public static IReadOnlySet<string> ReadCompletedTests(IEnumerable<string> trxPaths) =>
        ReadTests(trxPaths, passedOnly: true);

    // Any-outcome variant: a test recorded in a TRX with outcome NotExecuted was selected by the
    // run and deliberately skipped (static Skip, opt-in fact). It is ACCOUNTED FOR in coverage -
    // present in the run's report - without counting as executed evidence.
    public static IReadOnlySet<string> ReadRecordedTests(IEnumerable<string> trxPaths) =>
        ReadTests(trxPaths, passedOnly: false);

    private static IReadOnlySet<string> ReadTests(IEnumerable<string> trxPaths, bool passedOnly)
    {
        var tests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in trxPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
            {
                continue;
            }

            var document = XDocument.Load(path, LoadOptions.None);
            var definitions = document
                .Descendants()
                .Where(element => element.Name.LocalName == "UnitTest")
                .Select(element =>
                {
                    var id = (string?)element.Attribute("id");
                    var method = element.Descendants().FirstOrDefault(child => child.Name.LocalName == "TestMethod");
                    var className = (string?)method?.Attribute("className");
                    var methodName = (string?)method?.Attribute("name");
                    var identity = !string.IsNullOrWhiteSpace(className) && !string.IsNullOrWhiteSpace(methodName)
                        ? $"{className}.{methodName}"
                        : (string?)element.Attribute("name");
                    return (Id: id, Identity: identity);
                })
                .Where(definition => !string.IsNullOrWhiteSpace(definition.Id) && !string.IsNullOrWhiteSpace(definition.Identity))
                .ToDictionary(definition => definition.Id!, definition => definition.Identity!, StringComparer.OrdinalIgnoreCase);

            foreach (var result in document.Descendants().Where(element => element.Name.LocalName == "UnitTestResult"))
            {
                var outcome = (string?)result.Attribute("outcome");
                if (passedOnly && !string.Equals(outcome, "Passed", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var id = (string?)result.Attribute("testId");
                var displayName = (string?)result.Attribute("testName");
                AddNormalized(tests, displayName);
                if (id is not null && definitions.TryGetValue(id, out var definition))
                {
                    AddNormalized(tests, definition);
                }
            }
        }

        return tests;
    }

    public static TestCoverageInvariantResult Evaluate(
        IReadOnlySet<string> candidateDiscoveredTests,
        IReadOnlyList<TestPartitionCoverage> partitions,
        IReadOnlySet<string>? mainDiscoveredTests = null,
        IReadOnlyList<string>? deletedTestFiles = null,
        string? currentAttemptId = null)
    {
        if (candidateDiscoveredTests.Count == 0)
        {
            return new TestCoverageInvariantResult(
                false,
                "trusted discovery returned zero tests",
                [],
                [],
                AcceptanceFailureClassifications.StructuralCoverageFailed,
                []);
        }

        partitions = partitions
            .Select((partition, index) => (Partition: partition, Index: index))
            .Where(item =>
                string.IsNullOrWhiteSpace(currentAttemptId) ||
                string.IsNullOrWhiteSpace(item.Partition.AttemptId) ||
                item.Partition.AttemptId.Equals(currentAttemptId, StringComparison.OrdinalIgnoreCase) ||
                item.Partition.IsExplicitCrossAttemptReuse)
            .GroupBy(item => item.Partition.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(item => item.Partition.RunOrdinal)
                .ThenBy(item => item.Index)
                .Last()
                .Partition)
            .ToArray();
        var emptyPartitions = new List<string>();
        var completedTests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var accountedTests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var partition in partitions)
        {
            var executed = ReadCompletedTests(partition.TestResultPaths);
            if (!partition.Completed || executed.Count == 0)
            {
                emptyPartitions.Add(partition.Name);
                continue;
            }

            completedTests.UnionWith(executed);
            accountedTests.UnionWith(ReadRecordedTests(partition.TestResultPaths));
        }

        var executedTests = candidateDiscoveredTests
            .Where(discovered => completedTests.Any(completed => IdentitiesMatch(discovered, completed)))
            .OrderBy(identity => identity, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var missing = candidateDiscoveredTests
            .Where(discovered => !accountedTests.Any(accounted => IdentitiesMatch(discovered, accounted)))
            .OrderBy(identity => identity, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (mainDiscoveredTests is not null)
        {
            var deletedClassNames = (deletedTestFiles ?? [])
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToArray();
            var deletedMainTestCount = mainDiscoveredTests.Count(mainTest =>
                deletedClassNames.Any(className =>
                    IdentityBelongsToDeletedTestFile(mainTest, className!)));
            var unmatchedDeletedFileCount = Math.Min(
                deletedClassNames.Count(className =>
                    !mainDiscoveredTests.Any(mainTest =>
                        IdentityBelongsToDeletedTestFile(mainTest, className!))),
                mainDiscoveredTests.Count(mainTest =>
                    !HasClassQualifiedIdentity(mainTest) &&
                    !deletedClassNames.Any(className =>
                        IdentityBelongsToDeletedTestFile(mainTest, className!))));
            var minimumCandidateCount = Math.Max(
                0,
                mainDiscoveredTests.Count - deletedMainTestCount - unmatchedDeletedFileCount);
            if (candidateDiscoveredTests.Count < minimumCandidateCount)
            {
                missing.Add(
                    $"cross-generation-count:candidate={candidateDiscoveredTests.Count},minimum={minimumCandidateCount},main={mainDiscoveredTests.Count},deleted={deletedMainTestCount + unmatchedDeletedFileCount}");
            }
        }

        var passed = emptyPartitions.Count == 0 && missing.Count == 0;
        var summary = passed
            ? $"structural coverage complete: discovered={candidateDiscoveredTests.Count}, executed={executedTests.Length}, partitions={partitions.Count}"
            : $"structural coverage failed: discovered={candidateDiscoveredTests.Count}, executed={executedTests.Length}, missing={missing.Count}, emptyPartitions={emptyPartitions.Count}";
        var failureClassification = passed
            ? null
            : partitions.Any(partition =>
                emptyPartitions.Contains(partition.Name, StringComparer.OrdinalIgnoreCase) &&
                partition.HasEnvironmentInterferenceEvidence)
                ? AcceptanceFailureClassifications.GateEnvironmentInterference
                : AcceptanceFailureClassifications.StructuralCoverageFailed;
        return new TestCoverageInvariantResult(
            passed,
            summary,
            missing,
            emptyPartitions,
            failureClassification,
            executedTests);
    }

    private static bool IdentitiesMatch(string left, string right)
    {
        var normalizedLeft = NormalizeIdentity(left);
        var normalizedRight = NormalizeIdentity(right);
        return normalizedLeft.Equals(normalizedRight, StringComparison.OrdinalIgnoreCase) ||
            normalizedLeft.EndsWith($".{normalizedRight}", StringComparison.OrdinalIgnoreCase) ||
            normalizedRight.EndsWith($".{normalizedLeft}", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IdentityBelongsToDeletedTestFile(string identity, string className)
    {
        var normalized = NormalizeIdentity(identity);
        return normalized.StartsWith($"{className}.", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains($".{className}.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasClassQualifiedIdentity(string identity)
    {
        var normalized = NormalizeIdentity(identity);
        var separator = normalized.LastIndexOf('.');
        return separator > 0 && separator < normalized.Length - 1;
    }

    private static bool IsBareTestListDiagnostic(string line) =>
        BareTestListDiagnosticPrefixes.Any(prefix =>
            line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
        line.StartsWith("[xUnit.net", StringComparison.OrdinalIgnoreCase) ||
        line.All(character => character is '-' or '=' or '_');

    private static void AddNormalized(ISet<string> tests, string? value)
    {
        var normalized = NormalizeIdentity(value);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            tests.Add(normalized);
        }
    }

    private static string NormalizeIdentity(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        // Captured MTP discovery output can render theory arguments with an ASCII '-'
        // while the TRX preserves the original Unicode dash in an otherwise identical identity.
        // Canonicalize the common typographic dash variants so discovery and execution
        // receipts remain comparable.
        var requiresDashNormalization = false;
        foreach (var character in normalized)
        {
            if (character is '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2015')
            {
                requiresDashNormalization = true;
                break;
            }
        }

        if (!requiresDashNormalization)
        {
            return normalized;
        }

        return string.Create(normalized.Length, normalized, static (destination, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                destination[index] = source[index] switch
                {
                    '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2015' => '-',
                    _ => source[index]
                };
            }
        });
    }
}
