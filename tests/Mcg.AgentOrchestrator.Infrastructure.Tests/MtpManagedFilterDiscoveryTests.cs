using System.Text.Json;
using static MtpTestRunnerScriptTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class MtpManagedFilterDiscoveryTests
{
    [Xunit.Fact]
    public void BooleanFilters_SelectExactManagedTestSets()
    {
        var classA = DiscoverManagedTests("--filter-class", "*ConductorDriverTestsStaleFindingRouting*");
        var classB = DiscoverManagedTests("--filter-class", "*MtpNoBuildReceiptIdentityTests*");
        Assert.NotEmpty(classA);
        Assert.NotEmpty(classB);

        var union = DiscoverManagedTests(TranslateMtpFilter(
            "FullyQualifiedName~ConductorDriverTestsStaleFindingRouting|FullyQualifiedName~MtpNoBuildReceiptIdentityTests"));
        Assert.Equal(
            classA.Keys.Union(classB.Keys).Order(StringComparer.Ordinal),
            union.Keys.Order(StringComparer.Ordinal));

        var matchingMethods = DiscoverManagedTests("--filter-method", "*StaleTesterFinding*");
        var intersection = DiscoverManagedTests(TranslateMtpFilter(
            "FullyQualifiedName~ConductorDriverTestsStaleFindingRouting&Name~StaleTesterFinding"));
        Assert.Equal(
            classA.Keys.Intersect(matchingMethods.Keys).Order(StringComparer.Ordinal),
            intersection.Keys.Order(StringComparer.Ordinal));

        var oldFlattened = DiscoverManagedTests(
            "--filter-class", "*ConductorDriverTestsStaleFindingRouting*",
            "--filter-method", "*StaleTesterFinding*");
        var denotedCrossKindUnion = classA.Keys.Union(matchingMethods.Keys).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(oldFlattened);
        Assert.True(
            oldFlattened.Keys.ToHashSet(StringComparer.Ordinal).IsProperSubsetOf(denotedCrossKindUnion),
            "The recorded pre-fix argv must select a nonempty strict subset of the requested union.");

        var rejection = RejectMtpFilter(
            "FullyQualifiedName~ConductorDriverTestsStaleFindingRouting|Name~StaleTesterFinding");
        Assert.Contains("single invocation can OR only class alternatives or only method alternatives", rejection, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void ManifestFilters_TranslateAndKeepExclusionSemantics()
    {
        using var invocationCounters = InvocationCounterScope.Begin();
        var discoveriesBefore = _discoveryInvocations;
        var launchesBefore = _powerShellInvocations;
        var root = RepositoryRoot();
        var manifestPath = Path.Combine(root, "config", "acceptance-manifest.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var engine = manifest.RootElement.GetProperty("engine");
        var configuredCount = engine.GetProperty("infrastructureTestLanes").GetArrayLength()
            + engine.GetProperty("localTestPartitions")
                .EnumerateArray()
                .Sum(partition => partition.TryGetProperty("additionalFilters", out var filters) ? filters.GetArrayLength() : 0);

        string[] extraFilters =
        [
            "FullyQualifiedName~ConductorDriverTestsStaleFindingRouting&Name~StaleTesterFinding",
            "FullyQualifiedName~ConductorDriverTestsStaleFindingRouting&Name!~StaleTesterFinding",
            "FullyQualifiedName~MtpTestRunnerScriptTests",
            "FullyQualifiedName~MtpTestRunnerScriptTestsManagedProjectRebuild",
            "FullyQualifiedName~MtpTestRunnerScriptTests&FullyQualifiedName!~MtpTestRunnerScriptTestsManagedProjectRebuild",
            "FullyQualifiedName~ConductorCrossTickTests",
            "FullyQualifiedName~ConductorCrossTickTests&Category!=CrossTick"
        ];

        var module = Path.Combine(root, "scripts", "MtpTestRunner.psm1").Replace("'", "''", StringComparison.Ordinal);
        var escapedManifest = manifestPath.Replace("'", "''", StringComparison.Ordinal);
        var escapedExtras = JsonSerializer.Serialize(extraFilters).Replace("'", "''", StringComparison.Ordinal);
        var command = $"Import-Module '{module}' -Force; " +
            $"$manifest = Get-Content -LiteralPath '{escapedManifest}' -Raw | ConvertFrom-Json; " +
            $"$extras = ConvertFrom-Json -InputObject '{escapedExtras}'; " +
            "$filters = @($manifest.engine.infrastructureTestLanes | ForEach-Object { $_.filter }) + " +
            "@($manifest.engine.localTestPartitions | ForEach-Object { @($_.additionalFilters) }) + $extras; " +
            "$result = @($filters | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { " +
            "[ordered]@{ filter = $_; args = @(ConvertTo-MtpFilterArguments -Filter $_) } }); " +
            "ConvertTo-Json -InputObject $result -Depth 4 -Compress";
        var result = RunPowerShellCommand(root, command);

        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        using var translated = JsonDocument.Parse(result.Stdout.Trim());
        Assert.Equal(configuredCount + extraFilters.Length, translated.RootElement.GetArrayLength());
        var cases = translated.RootElement.EnumerateArray()
            .Select(item => (
                Filter: item.GetProperty("filter").GetString()!,
                Arguments: item.GetProperty("args").EnumerateArray().Select(value => value.GetString()!).ToArray()))
            .ToArray();
        foreach (var group in cases.GroupBy(item => item.Filter, StringComparer.Ordinal))
        {
            Assert.All(group, item => Assert.Equal(group.First().Arguments, item.Arguments));
        }
        var universe = DiscoverManagedTestCatalog(allowEmpty: false);
        var signatures = cases.Select(item => MtpFilterArgumentSemantics.KindSignature(item.Arguments))
            .Distinct(StringComparer.Ordinal).ToArray();
        var representatives = cases.GroupBy(item => MtpFilterArgumentSemantics.KindSignature(item.Arguments), StringComparer.Ordinal)
            .Select(group =>
            {
                var representative = group.FirstOrDefault(item => MtpFilterArgumentSemantics.IsNonVacuous(item.Arguments, universe));
                if (representative.Arguments is null)
                {
                    representative = group.First();
                }
                Assert.NotNull(representative.Arguments);
                return (Signature: group.Key, representative.Filter, representative.Arguments);
            }).ToArray();
        Assert.True(representatives.Length < configuredCount, "Discovery representatives must be fewer than manifest filters.");
        Assert.Empty(MtpFilterArgumentSemantics.UncoveredSignatures(signatures, representatives.Select(item => item.Signature)));
        foreach (var requiredKind in new[]
                 { "--filter-class", "--filter-not-class", "--filter-method", "--filter-not-method", "--filter-not-trait" })
        {
            Assert.Contains(representatives, item => item.Arguments.Contains(requiredKind, StringComparer.Ordinal));
        }
        Assert.Contains(representatives, item => item.Signature.Contains("|nested", StringComparison.Ordinal));
        foreach (var representative in representatives)
        {
            var expectedReal = MtpFilterArgumentSemantics.Select(representative.Arguments, universe)
                .Select(test => test.Uid).Order(StringComparer.Ordinal);
            var actualReal = DiscoverManagedTests(allowEmpty: true, representative.Arguments)
                .Keys.Order(StringComparer.Ordinal);
            Assert.Equal(expectedReal, actualReal);
        }
        foreach (var item in cases.Take(configuredCount))
        {
            var filter = item.Filter;
            var arguments = item.Arguments;
            Assert.NotEmpty(arguments);
            Assert.True(arguments.Length % 2 == 0, filter);

            var expected = universe
                .Where(test => ManagedFilterExpressionEvaluator.Evaluate(filter, test))
                .Select(test => test.Uid)
                .Order(StringComparer.Ordinal);
            var actual = MtpFilterArgumentSemantics.Select(arguments, universe)
                .Select(test => test.Uid)
                .Order(StringComparer.Ordinal);
            Assert.Equal(expected, actual);
        }

        IReadOnlyDictionary<string, string> Select(string filter) =>
            MtpFilterArgumentSemantics.Select(cases.First(item => item.Filter == filter).Arguments, universe)
                .ToDictionary(test => test.Uid, test => test.DisplayName, StringComparer.Ordinal);
        var broadClass = Select("FullyQualifiedName~MtpTestRunnerScriptTests");
        var excludedClass = Select("FullyQualifiedName~MtpTestRunnerScriptTestsManagedProjectRebuild");
        var withoutRebuild = Select("FullyQualifiedName~MtpTestRunnerScriptTests&FullyQualifiedName!~MtpTestRunnerScriptTestsManagedProjectRebuild");
        Assert.NotEmpty(excludedClass);
        Assert.Equal(
            broadClass.Keys.Except(excludedClass.Keys).Order(StringComparer.Ordinal),
            withoutRebuild.Keys.Order(StringComparer.Ordinal));

        var crossTick = Select("FullyQualifiedName~ConductorCrossTickTests");
        Assert.NotEmpty(crossTick);
        var withoutCrossTickTrait = Select("FullyQualifiedName~ConductorCrossTickTests&Category!=CrossTick");
        Assert.Empty(withoutCrossTickTrait);
        Assert.Equal(1 + representatives.Length, _discoveryInvocations - discoveriesBefore);
        Assert.Equal(1, _powerShellInvocations - launchesBefore);
    }

}
