using System.Reflection;
using System.Text.RegularExpressions;

public sealed class GoalAcceptanceVerifierSplitFactParityTests
{
    private const string OriginalBuildSlotClass = nameof(GoalAcceptanceVerifierDotnetBuildSlotTests);

    // The fragments the original class was split into. The ParentSuffix naming convention also gives new, unrelated
    // subclasses this prefix, so parity is pinned to these names rather than to the prefix.
    private static readonly HashSet<string> OriginalSplitFragments = new(StringComparer.Ordinal)
    {
        nameof(AcceptanceOverlappedCheckSchedulingTests),
        nameof(GoalAcceptanceVerifierDotnetBuildSlotTestsAdvisoryChecks),
        nameof(GoalAcceptanceVerifierDotnetBuildSlotTestsConcurrentShardScheduling),
        nameof(GoalAcceptanceVerifierDotnetBuildSlotTestsFocusedEvidence),
        nameof(GoalAcceptanceVerifierDotnetBuildSlotTestsGateHeartbeat),
        nameof(GoalAcceptanceVerifierDotnetBuildSlotTestsPolicyShardScope),
        nameof(GoalAcceptanceVerifierDotnetBuildSlotTestsRunnerInvocationAndTrx),
        nameof(GoalAcceptanceVerifierDotnetBuildSlotTestsShardReceipts),
        nameof(GoalAcceptanceVerifierDotnetBuildSlotTestsSharedApparatusInvalidation),
        nameof(GoalAcceptanceVerifierDotnetBuildSlotTestsSlotGateJobResources),
        nameof(GoalAcceptanceVerifierDotnetBuildSlotTestsTestTamperGuard),
        nameof(GoalAcceptanceVerifierDotnetBuildSlotTestsTrustedBaselineDiscovery),
        nameof(GoalAcceptanceVerifierDotnetBuildSlotTestsVerdictAndBuildCache),
    };

    [Xunit.Fact]
    public void SplitPreservesDeclaredFactAndTheoryMethodSet()
    {
        var expected = LoadBaseline();
        var expectedOwners = expected
            .Select(identity => identity[..identity.IndexOf('.', StringComparison.Ordinal)])
            .ToHashSet(StringComparer.Ordinal);
        var affectedTypes = Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(type => IsAffectedConcreteTestClass(type, expectedOwners))
            .ToArray();
        var sourceByType = LoadSourceByType(
            SourceDirectory(),
            affectedTypes.Select(type => type.Name).ToHashSet(StringComparer.Ordinal));

        var actual = affectedTypes
            .SelectMany(type => type
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(IsFactOrTheory)
                .SelectMany(method => TestCaseIdentities(type, method, sourceByType)))
            .OrderBy(identity => identity, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    [Xunit.Fact]
    public void SplitPreservesCollectionConcurrencyContracts()
    {
        var assemblyTypes = Assembly.GetExecutingAssembly().GetTypes();
        var buildSlotFragments = assemblyTypes
            .Where(type =>
                type.IsClass &&
                !type.IsAbstract &&
                type.BaseType == typeof(GoalAcceptanceVerifierDotnetBuildSlotTests))
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.Subset(
            buildSlotFragments.Select(type => type.Name).ToHashSet(StringComparer.Ordinal),
            OriginalSplitFragments);
        Assert.All(
            buildSlotFragments,
            fragment => Assert.Equal(TestCollections.JobAccounting, CollectionName(fragment)));
        Assert.Equal(TestCollections.JobAccounting, CollectionName(typeof(RealProcessShardAlphaSmokeTests)));
        Assert.Equal(TestCollections.JobAccounting, CollectionName(typeof(RealProcessShardBetaSmokeTests)));
        Assert.Equal(
            TestCollections.DotnetBuildEnvironmentManagerStaticHooks,
            CollectionName(typeof(LocalProcessVerifierStaticHookTests)));
        Assert.True(CollectionDisablesParallelization(typeof(DotnetBuildEnvironmentManagerStaticHooksCollection)));

        Assert.Equal(
            TestCollections.GoalAcceptanceVerifier,
            CollectionName(typeof(GoalAcceptanceVerifierTests)));
        Assert.Equal(
            TestCollections.GoalAcceptanceVerifier,
            CollectionName(typeof(AcceptanceOutputCaptureTests)));
        Assert.Equal(
            TestCollections.GoalAcceptanceVerifier,
            CollectionName(typeof(HermeticVerificationEnvironmentTests)));
        Assert.Null(CollectionName(typeof(GoalAcceptanceVerifierDotnetBuildSlotTests)));
    }

    private static bool IsAffectedConcreteTestClass(Type type, IReadOnlySet<string> expectedOwners) =>
        type.IsClass &&
        !type.IsAbstract &&
        (expectedOwners.Contains(type.Name) || OriginalSplitFragments.Contains(type.Name));

    private static bool IsFactOrTheory(MethodInfo method) =>
        method.GetCustomAttributes(inherit: false).Any(attribute => attribute is Xunit.FactAttribute);

    private static IEnumerable<string> TestCaseIdentities(
        Type type,
        MethodInfo method,
        IReadOnlyDictionary<string, string> sourceByType)
    {
        if (!sourceByType.TryGetValue(type.Name, out var source))
        {
            throw new InvalidOperationException(
                $"Could not find one source file declaring {type.Name}.");
        }

        var methodMatch = Regex.Match(
            source,
            $@"(?m)(?<attributes>(?:^[ \t]*\[[^\r\n]+\]\r?\n)+)[ \t]*public[ \t]+(?:async[ \t]+)?[^\r\n(]+[ \t]+{Regex.Escape(method.Name)}[ \t]*\((?<parameters>[^)]*)\)[ \t]*(?:\r?\n[ \t]*)?(?:\{{|=>)");
        if (!methodMatch.Success)
        {
            throw new InvalidOperationException(
                $"Could not find the source declaration for {type.Name}.{method.Name}.");
        }

        var identity = $"{NormalizeOwner(type)}.{method.Name}";
        var parameters = Regex.Replace(
            methodMatch.Groups["parameters"].Value,
            @"\s+",
            " ").Trim();
        var dataAttributes = Regex.Matches(
            methodMatch.Groups["attributes"].Value,
            @"(?m)^[ \t]*\[(?:Xunit\.)?(?<kind>InlineData|MemberData|ClassData)\((?<args>[^\r\n]*)\)\][ \t]*\r?$");

        if (dataAttributes.Count == 0)
        {
            yield return parameters.Length == 0 ? identity : $"{identity}({parameters})";
            yield break;
        }

        foreach (Match dataAttribute in dataAttributes)
        {
            yield return $"{identity}({parameters})|" +
                $"{dataAttribute.Groups["kind"].Value}({dataAttribute.Groups["args"].Value.Trim()})";
        }
    }

    private static IReadOnlyDictionary<string, string> LoadSourceByType(
        string sourceDirectory,
        IReadOnlySet<string> affectedTypeNames) =>
        Directory
            .EnumerateFiles(sourceDirectory, "*.cs", SearchOption.TopDirectoryOnly)
            .Select(path => File.ReadAllText(path))
            .SelectMany(source => Regex
                .Matches(source, @"\bpublic\s+sealed\s+class\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\b")
                .Select(match => new
                {
                    Name = match.Groups["name"].Value,
                    Source = source,
                }))
            .Where(item => affectedTypeNames.Contains(item.Name))
            .ToDictionary(item => item.Name, item => item.Source, StringComparer.Ordinal);

    private static string NormalizeOwner(Type type) =>
        type.Name.StartsWith(OriginalBuildSlotClass, StringComparison.Ordinal)
            ? OriginalBuildSlotClass
            : type.Name;

    private static string? CollectionName(Type type) =>
        type.CustomAttributes
            .SingleOrDefault(attribute => attribute.AttributeType == typeof(Xunit.CollectionAttribute))?
            .ConstructorArguments
            .Single()
            .Value as string;

    private static bool CollectionDisablesParallelization(Type type) =>
        type.GetCustomAttribute<Xunit.CollectionDefinitionAttribute>()?.DisableParallelization == true;

    private static string SourceDirectory(
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "") =>
        Path.GetDirectoryName(sourceFilePath)
            ?? throw new InvalidOperationException("Parity test source path has no directory.");

    private static string[] LoadBaseline(
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "") =>
        File.ReadAllLines(Path.Combine(
                Path.GetDirectoryName(sourceFilePath)
                    ?? throw new InvalidOperationException("Parity test source path has no directory."),
                "GoalAcceptanceVerifierSplitFactBaseline.txt"))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToArray();
}
