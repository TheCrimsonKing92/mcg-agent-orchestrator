using System.Text;
using System.Text.Json;
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
    IReadOnlyList<string> ExecutedTests,
    IReadOnlyList<TestCoverageIdentityMismatch>? IdentityMismatches = null);

internal sealed record TestCoverageIdentityMismatch(
    string Discovered,
    string? Executed);

internal sealed record TestDiscoverySnapshot(
    IReadOnlySet<string> Tests,
    IReadOnlyDictionary<string, string>? SourceFilesByTest);

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

    public static IReadOnlySet<string> ParseDiscoveredTests(string output, bool bareTestList = false) =>
        ParseDiscovery(output, bareTestList).Tests;

    public static TestDiscoverySnapshot ParseDiscovery(
        string output,
        bool bareTestList = false,
        string? repositoryRoot = null)
    {
        var structured = TryParseStructuredDiscovery(output, repositoryRoot);
        if (structured is not null)
        {
            return structured;
        }

        return new TestDiscoverySnapshot(ParseTextDiscovery(output, bareTestList), null);
    }

    private static IReadOnlySet<string> ParseTextDiscovery(string output, bool bareTestList)
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

    private static TestDiscoverySnapshot? TryParseStructuredDiscovery(
        string output,
        string? repositoryRoot)
    {
        var schemaVersion = output.IndexOf("\"schemaVersion\"", StringComparison.Ordinal);
        var jsonStart = schemaVersion < 0 ? -1 : output.LastIndexOf('{', schemaVersion);
        if (jsonStart < 0 ||
            !output.AsSpan(schemaVersion).Contains("\"tests\"", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var utf8 = Encoding.UTF8.GetBytes(output[jsonStart..]);
            var reader = new Utf8JsonReader(utf8);
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (!root.TryGetProperty("tests", out var discoveredTests) ||
                discoveredTests.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Structured test discovery did not contain a tests array.");
            }

            var tests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sourceFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var ambiguousSourceFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var discoveredTest in discoveredTests.EnumerateArray())
            {
                if (!discoveredTest.TryGetProperty("displayName", out var displayNameElement))
                {
                    continue;
                }

                var identity = NormalizeIdentity(displayNameElement.GetString());
                if (string.IsNullOrWhiteSpace(identity))
                {
                    continue;
                }

                tests.Add(identity);
                if (ambiguousSourceFiles.Contains(identity) ||
                    !discoveredTest.TryGetProperty("location", out var location) ||
                    !location.TryGetProperty("file", out var fileElement))
                {
                    continue;
                }

                var sourceFile = NormalizeSourcePath(fileElement.GetString(), repositoryRoot);
                if (string.IsNullOrWhiteSpace(sourceFile))
                {
                    continue;
                }

                if (sourceFiles.TryGetValue(identity, out var existingSourceFile) &&
                    !existingSourceFile.Equals(sourceFile, StringComparison.OrdinalIgnoreCase))
                {
                    sourceFiles.Remove(identity);
                    ambiguousSourceFiles.Add(identity);
                    continue;
                }

                sourceFiles[identity] = sourceFile;
            }

            return new TestDiscoverySnapshot(tests, sourceFiles);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Structured test discovery JSON could not be parsed.", exception);
        }
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
        string? currentAttemptId = null,
        IReadOnlyDictionary<string, string>? mainDiscoveredTestSourceFiles = null,
        IReadOnlyList<string>? sanctionedRemovedTests = null)
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
        var recordedTests = candidateDiscoveredTests
            .Where(discovered => accountedTests.Any(accounted => IdentitiesMatch(discovered, accounted)))
            .OrderBy(identity => identity, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var missing = candidateDiscoveredTests
            .Where(discovered => !accountedTests.Any(accounted => IdentitiesMatch(discovered, accounted)))
            .OrderBy(identity => identity, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var identityMismatches = missing
            .Select(discovered => new TestCoverageIdentityMismatch(
                discovered,
                FindClosestExecutedForm(discovered, completedTests)))
            .ToArray();
        var attributionReceipts = new List<string>();
        string? crossGenerationCountReceipt = null;

        if (mainDiscoveredTests is not null)
        {
            var normalizedDeletedTestFiles = (deletedTestFiles ?? [])
                .Select(path => NormalizeSourcePath(path, repositoryRoot: null))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var deletedClassNames = normalizedDeletedTestFiles
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToArray();
            // Structured MTP discovery identifies the source file directly. Prefer that exact
            // attribution over the legacy filename/class-name heuristic. Location metadata is
            // optional, however, so an unattributed deleted file retains at most the legacy
            // one-test credit and emits a named receipt instead of silently tightening the gate.
            var creditedMainTests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var anonymousAttributionCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unattributedDeletedFiles = new List<string>();
            var unmatchedDeletedFileCount = 0;
            if (mainDiscoveredTestSourceFiles is not null)
            {
                foreach (var deletedTestFile in normalizedDeletedTestFiles)
                {
                    var attributedTests = mainDiscoveredTests.Where(mainTest =>
                            mainDiscoveredTestSourceFiles.TryGetValue(mainTest, out var sourceFile) &&
                            IsUsableRepositoryRelativeSourcePath(sourceFile) &&
                            sourceFile.Equals(deletedTestFile, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    var attributedTestCount = attributedTests.Length;
                    if (attributedTestCount > 0)
                    {
                        creditedMainTests.UnionWith(attributedTests);
                    }
                    else
                    {
                        unattributedDeletedFiles.Add(deletedTestFile);
                    }
                }

                var testsWithoutUsableAttribution = mainDiscoveredTests.Count(mainTest =>
                    !mainDiscoveredTestSourceFiles.TryGetValue(mainTest, out var sourceFile) ||
                    !IsUsableRepositoryRelativeSourcePath(sourceFile));
                anonymousAttributionCandidates.UnionWith(mainDiscoveredTests.Where(mainTest =>
                    !mainDiscoveredTestSourceFiles.TryGetValue(mainTest, out var sourceFile) ||
                    !IsUsableRepositoryRelativeSourcePath(sourceFile)));
                unmatchedDeletedFileCount = Math.Min(
                    unattributedDeletedFiles.Count,
                    testsWithoutUsableAttribution);
            }
            else
            {
                creditedMainTests.UnionWith(mainDiscoveredTests.Where(mainTest =>
                    deletedClassNames.Any(className =>
                        IdentityBelongsToDeletedTestFile(mainTest, className!))));
                anonymousAttributionCandidates.UnionWith(mainDiscoveredTests.Where(mainTest =>
                    !HasClassQualifiedIdentity(mainTest) &&
                    !deletedClassNames.Any(className =>
                        IdentityBelongsToDeletedTestFile(mainTest, className!))));
                unmatchedDeletedFileCount = Math.Min(
                    deletedClassNames.Count(className =>
                        !mainDiscoveredTests.Any(mainTest =>
                            IdentityBelongsToDeletedTestFile(mainTest, className!))),
                    mainDiscoveredTests.Count(mainTest =>
                        !HasClassQualifiedIdentity(mainTest) &&
                        !deletedClassNames.Any(className =>
                            IdentityBelongsToDeletedTestFile(mainTest, className!))));
            }

            foreach (var declaredIdentity in (sanctionedRemovedTests ?? [])
                .Where(identity => !string.IsNullOrWhiteSpace(identity))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var corroboratedTests = mainDiscoveredTests
                    .Where(mainTest =>
                        DeclaredIdentityMatchesDiscoveredTest(mainTest, declaredIdentity) &&
                        !candidateDiscoveredTests.Any(candidateTest => IdentitiesMatch(mainTest, candidateTest)))
                    .ToArray();
                creditedMainTests.UnionWith(corroboratedTests);
                attributionReceipts.Add(
                    $"cross-generation-attribution:source=declared-test-removal,identity={declaredIdentity},status={(corroboratedTests.Length > 0 ? "corroborated" : "unmatched")},credit={corroboratedTests.Length}");
            }

            unmatchedDeletedFileCount = Math.Max(
                0,
                unmatchedDeletedFileCount - creditedMainTests.Count(anonymousAttributionCandidates.Contains));
            for (var index = 0; index < unattributedDeletedFiles.Count; index++)
            {
                attributionReceipts.Add(
                    $"cross-generation-attribution:source=mtp-json-location,file={unattributedDeletedFiles[index]},status=unattributed,fallback=legacy-one-per-file,credit={(index < unmatchedDeletedFileCount ? 1 : 0)}");
            }
            var deletedMainTestCount = creditedMainTests.Count;
            var minimumCandidateCount = Math.Max(
                0,
                mainDiscoveredTests.Count - deletedMainTestCount - unmatchedDeletedFileCount);
            crossGenerationCountReceipt =
                $"cross-generation-count:candidate={candidateDiscoveredTests.Count},minimum={minimumCandidateCount},main={mainDiscoveredTests.Count},deleted={deletedMainTestCount + unmatchedDeletedFileCount}";
            if (candidateDiscoveredTests.Count < minimumCandidateCount)
            {
                missing.Add(crossGenerationCountReceipt);
            }
        }

        var passed = emptyPartitions.Count == 0 && missing.Count == 0;
        var summary = passed
            ? $"structural coverage complete: discovered={candidateDiscoveredTests.Count}, executed={executedTests.Length}, recorded={recordedTests.Length}, partitions={partitions.Count}"
            : $"structural coverage failed: discovered={candidateDiscoveredTests.Count}, executed={executedTests.Length}, recorded={recordedTests.Length}, missing={missing.Count}, emptyPartitions={emptyPartitions.Count}";
        if (crossGenerationCountReceipt is not null)
        {
            summary += $"; {crossGenerationCountReceipt}";
        }
        if (attributionReceipts.Count > 0)
        {
            summary += $"; {string.Join("; ", attributionReceipts)}";
        }
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
            executedTests,
            identityMismatches);
    }

    private static string? FindClosestExecutedForm(string discovered, IReadOnlySet<string> completedTests)
    {
        var discoveredMethod = NormalizeMethodName(GetIdentityMethodName(discovered));
        var discoveredClass = GetIdentityClassQualifier(discovered);
        if (string.IsNullOrWhiteSpace(discoveredMethod))
        {
            return null;
        }

        return completedTests
            .Where(executed =>
                IdentityClassQualifiersMatch(discoveredClass, GetIdentityClassQualifier(executed)) &&
                NormalizeMethodName(GetIdentityMethodName(executed)).Equals(
                    discoveredMethod,
                    StringComparison.OrdinalIgnoreCase))
            .OrderBy(executed => EditDistance(discovered, executed))
            .ThenBy(executed => executed, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static int EditDistance(string left, string right)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            current[0] = leftIndex;
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                var substitutionCost = char.ToUpperInvariant(left[leftIndex - 1]) ==
                    char.ToUpperInvariant(right[rightIndex - 1]) ? 0 : 1;
                current[rightIndex] = Math.Min(
                    Math.Min(current[rightIndex - 1] + 1, previous[rightIndex] + 1),
                    previous[rightIndex - 1] + substitutionCost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static bool IdentitiesMatch(string left, string right)
    {
        var normalizedLeft = NormalizeIdentity(left);
        var normalizedRight = NormalizeIdentity(right);
        return normalizedLeft.Equals(normalizedRight, StringComparison.OrdinalIgnoreCase) ||
            normalizedLeft.EndsWith($".{normalizedRight}", StringComparison.OrdinalIgnoreCase) ||
            normalizedRight.EndsWith($".{normalizedLeft}", StringComparison.OrdinalIgnoreCase);
    }

    private static bool DeclaredIdentityMatchesDiscoveredTest(string discoveredIdentity, string declaredIdentity)
    {
        if (IdentitiesMatch(discoveredIdentity, declaredIdentity))
        {
            return true;
        }

        var discoveredMethod = GetIdentityMethodName(discoveredIdentity);
        var declaredMethod = GetIdentityMethodName(declaredIdentity);
        return !string.IsNullOrWhiteSpace(discoveredMethod) &&
            !string.IsNullOrWhiteSpace(declaredMethod) &&
            NormalizeMethodName(discoveredMethod).Equals(
                NormalizeMethodName(declaredMethod),
                StringComparison.OrdinalIgnoreCase);
    }

    private static string GetIdentityMethodName(string identity)
    {
        var normalized = NormalizeIdentity(identity);
        var argumentsIndex = normalized.IndexOf('(');
        if (argumentsIndex >= 0)
        {
            normalized = normalized[..argumentsIndex];
        }

        var separatorIndex = Math.Max(normalized.LastIndexOf('.'), normalized.LastIndexOf(':'));
        return (separatorIndex >= 0 ? normalized[(separatorIndex + 1)..] : normalized).Trim();
    }

    private static string GetIdentityClassQualifier(string identity)
    {
        var normalized = NormalizeIdentity(identity);
        var argumentsIndex = normalized.IndexOf('(');
        if (argumentsIndex >= 0)
        {
            normalized = normalized[..argumentsIndex];
        }

        var separatorIndex = Math.Max(normalized.LastIndexOf('.'), normalized.LastIndexOf(':'));
        return separatorIndex > 0 ? normalized[..separatorIndex].Trim() : string.Empty;
    }

    private static bool IdentityClassQualifiersMatch(string left, string right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        (left.Equals(right, StringComparison.OrdinalIgnoreCase) ||
         left.EndsWith($".{right}", StringComparison.OrdinalIgnoreCase) ||
         right.EndsWith($".{left}", StringComparison.OrdinalIgnoreCase));

    private static string NormalizeMethodName(string methodName)
    {
        var normalized = new StringBuilder(methodName.Length);
        foreach (var character in methodName)
        {
            if (char.IsLetterOrDigit(character))
            {
                normalized.Append(char.ToLowerInvariant(character));
            }
        }

        return normalized.ToString();
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

    private static bool IsUsableRepositoryRelativeSourcePath(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !Path.IsPathRooted(path) &&
        !path.Equals("..", StringComparison.Ordinal) &&
        !path.StartsWith("../", StringComparison.Ordinal);

    private static string NormalizeSourcePath(string? path, string? repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var normalized = path.Trim();
        if (!string.IsNullOrWhiteSpace(repositoryRoot) && Path.IsPathRooted(normalized))
        {
            var relative = Path.GetRelativePath(repositoryRoot, normalized);
            if (relative.Equals("..", StringComparison.Ordinal) ||
                relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                return string.Empty;
            }
            else
            {
                normalized = relative;
            }
        }

        return normalized.Replace('\\', '/').TrimStart('/');
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
