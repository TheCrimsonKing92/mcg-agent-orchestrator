using System.Xml.Linq;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record TestPartitionCoverage(
    string Name,
    bool Completed,
    IReadOnlyList<string> TestResultPaths);

internal sealed record TestCoverageInvariantResult(
    bool Passed,
    string Summary,
    IReadOnlyList<string> MissingTests,
    IReadOnlyList<string> EmptyPartitions);

internal static class TestCoverageInvariant
{
    public static IReadOnlySet<string> ParseDiscoveredTests(string output)
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

    public static IReadOnlySet<string> ReadCompletedTests(IEnumerable<string> trxPaths)
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
                if (!string.Equals(outcome, "Passed", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(outcome, "Failed", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var id = (string?)result.Attribute("testId");
                var identity = id is not null && definitions.TryGetValue(id, out var definition)
                    ? definition
                    : (string?)result.Attribute("testName");
                AddNormalized(tests, identity);
            }
        }

        return tests;
    }

    public static TestCoverageInvariantResult Evaluate(
        IReadOnlySet<string> candidateDiscoveredTests,
        IReadOnlyList<TestPartitionCoverage> partitions,
        IReadOnlySet<string>? mainDiscoveredTests = null,
        IReadOnlyList<string>? deletedTestFiles = null)
    {
        if (candidateDiscoveredTests.Count == 0)
        {
            return new TestCoverageInvariantResult(
                false,
                "trusted discovery returned zero tests",
                [],
                []);
        }

        var emptyPartitions = new List<string>();
        var completedTests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var partition in partitions)
        {
            var executed = ReadCompletedTests(partition.TestResultPaths);
            if (!partition.Completed || executed.Count == 0)
            {
                emptyPartitions.Add(partition.Name);
                continue;
            }

            completedTests.UnionWith(executed);
        }

        var missing = candidateDiscoveredTests
            .Where(discovered => !completedTests.Any(completed => IdentitiesMatch(discovered, completed)))
            .OrderBy(identity => identity, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (mainDiscoveredTests is not null)
        {
            var deletedClassNames = (deletedTestFiles ?? [])
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToArray();
            missing.AddRange(mainDiscoveredTests
                .Where(mainTest => !candidateDiscoveredTests.Any(candidate => IdentitiesMatch(mainTest, candidate)))
                .Where(mainTest => !deletedClassNames.Any(className =>
                    mainTest.Contains(className!, StringComparison.OrdinalIgnoreCase)))
                .Select(mainTest => $"main-only:{mainTest}"));
        }

        var passed = emptyPartitions.Count == 0 && missing.Count == 0;
        var summary = passed
            ? $"structural coverage complete: discovered={candidateDiscoveredTests.Count}, executed={completedTests.Count}, partitions={partitions.Count}"
            : $"structural coverage failed: discovered={candidateDiscoveredTests.Count}, executed={completedTests.Count}, missing={missing.Count}, emptyPartitions={emptyPartitions.Count}";
        return new TestCoverageInvariantResult(passed, summary, missing, emptyPartitions);
    }

    private static bool IdentitiesMatch(string left, string right)
    {
        var normalizedLeft = NormalizeIdentity(left);
        var normalizedRight = NormalizeIdentity(right);
        return normalizedLeft.Equals(normalizedRight, StringComparison.OrdinalIgnoreCase) ||
            normalizedLeft.EndsWith($".{normalizedRight}", StringComparison.OrdinalIgnoreCase) ||
            normalizedRight.EndsWith($".{normalizedLeft}", StringComparison.OrdinalIgnoreCase);
    }

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

        var parameterIndex = normalized.IndexOf('(');
        return parameterIndex > 0 ? normalized[..parameterIndex].Trim() : normalized;
    }
}
