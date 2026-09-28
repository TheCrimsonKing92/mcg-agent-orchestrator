using System.Text.RegularExpressions;

public sealed class ParallelSharedStateSourceGuardTests
{
    private static readonly Regex PolicyMutation = new(
        @"\bEnvironment\s*\.\s*SetEnvironmentVariable\s*\(\s*""MCG_ACCEPTANCE_(?:FULL_SHARDS|CHANGE_SCOPED)""",
        RegexOptions.Compiled);
    private static readonly Regex RegistryClear = new(
        @"\bWorkerProcessJobs\s*\.\s*ClearRegistryForTests\s*\(",
        RegexOptions.Compiled);
    private static readonly Regex ClassDeclaration = new(
        @"(?m)^[ \t]*(?:public|internal|private|protected)(?:\s+\w+)*\s+class\s+[A-Za-z_][A-Za-z_0-9]*",
        RegexOptions.Compiled);
    private static readonly Regex Collection = new(
        @"\[\s*(?:Xunit\.)?Collection\s*\(\s*(?<name>[^)]+)\)",
        RegexOptions.Compiled);
    private static readonly Regex SerialCollectionDefinition = new(
        @"\[\s*(?:Xunit\.)?CollectionDefinition\s*\(\s*(?<name>[^,]+),\s*DisableParallelization\s*=\s*true\s*\)",
        RegexOptions.Compiled);

    [Fact]
    public void TestSourcesDoNotMutateAcceptancePolicyOrClearParallelRegistry()
    {
        var testsRoot = FindTestsRoot();
        var sources = Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj"))
            .Where(path => !Path.GetFileName(path).Equals(nameof(ParallelSharedStateSourceGuardTests) + ".cs", StringComparison.Ordinal))
            .Select(path => (Path: Path.GetRelativePath(testsRoot, path), Text: File.ReadAllText(path)))
            .ToArray();

        var offenders = FindOffenders(sources);
        Assert.True(offenders.Count == 0,
            "Parallel test source mutates shared state: " + string.Join(", ", offenders));
    }

    [Fact]
    public void GuardDetectsTheOriginalParallelMutations()
    {
        var sources = new (string Path, string Text)[]
        {
            ("AcceptancePolicyShardPlannerTests.cs", "public sealed class AcceptancePolicyShardPlannerTests { void Fact() { Environment.SetEnvironmentVariable(\"MCG_ACCEPTANCE_FULL_SHARDS\", \"1\"); } }"),
            ("PlannerSamplingDispatchTests.cs", "public sealed class PlannerSamplingDispatchTests { void Fact() { WorkerProcessJobs.ClearRegistryForTests(); } }")
        };

        var offenders = FindOffenders(sources);
        Assert.Contains(offenders, offender => offender.StartsWith("AcceptancePolicyShardPlannerTests.cs:", StringComparison.Ordinal));
        Assert.Contains(offenders, offender => offender.StartsWith("PlannerSamplingDispatchTests.cs:", StringComparison.Ordinal));
    }

    private static List<string> FindOffenders(IEnumerable<(string Path, string Text)> sources)
    {
        var files = sources.ToArray();
        var serialCollections = files
            .GroupBy(file => ProjectName(file.Path), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .SelectMany(file => SerialCollectionDefinition.Matches(file.Text).Cast<Match>())
                    .Select(match => NormalizeCollectionName(match.Groups["name"].Value))
                    .ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);
        var offenders = new List<string>();

        foreach (var (path, source) in files)
        {
            foreach (Match match in PolicyMutation.Matches(source))
                offenders.Add($"{path}:{LineNumber(source, match.Index)} (policy environment)");

            foreach (Match match in RegistryClear.Matches(source))
            {
                var classMatch = ClassDeclaration.Matches(source[..match.Index]).Cast<Match>().LastOrDefault();
                var attributes = classMatch is null ? "" : AttributesBeforeClass(source, classMatch.Index);
                var collection = Collection.Matches(attributes).Cast<Match>().LastOrDefault();
                if (collection is null ||
                    !serialCollections[ProjectName(path)].Contains(NormalizeCollectionName(collection.Groups["name"].Value)))
                    offenders.Add($"{path}:{LineNumber(source, match.Index)} (parallel registry clear)");
            }
        }

        return offenders;
    }

    private static string NormalizeCollectionName(string name) =>
        name.Trim().Trim('"').Split('.').Last();

    private static string ProjectName(string path) =>
        path.Replace('\\', '/').Split('/')[0];

    private static string AttributesBeforeClass(string source, int classIndex)
    {
        var lines = source[..classIndex].Split('\n');
        var attributes = new List<string>();
        for (var index = lines.Length - 2; index >= 0; index--)
        {
            var line = lines[index].Trim();
            if (!line.StartsWith("[", StringComparison.Ordinal))
                break;
            attributes.Add(line);
        }
        return string.Join("\n", attributes);
    }

    private static int LineNumber(string text, int index) =>
        text[..index].Count(character => character == '\n') + 1;

    private static string FindTestsRoot([System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
    {
        var sourceDirectory = Path.GetDirectoryName(sourceFilePath);
        if (Directory.Exists(sourceDirectory) &&
            Path.GetFileName(sourceDirectory).Equals("Mcg.AgentOrchestrator.Infrastructure.Tests", StringComparison.Ordinal))
            return Directory.GetParent(sourceDirectory)!.FullName;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tests");
            if (Directory.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the checked-out tests directory.");
    }
}
