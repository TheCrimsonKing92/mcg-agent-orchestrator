using Mcg.AgentOrchestrator.Infrastructure;
using static FocusedEvidenceUnionPlanningTests;
using static MtpTestRunnerScriptTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class FocusedEvidenceUnionManagedTests
{
    [Xunit.Theory]
    [Xunit.InlineData("|")]
    [Xunit.InlineData(";")]
    public void DisjointMixedUnion_SelectsEveryIndependentlyDiscoveredIdentity(string separator)
    {
        var values = new[] { ClassA, ClassB, MethodA, MethodB };
        var expected = values.SelectMany(value => DiscoverManagedTests(
            value.Contains('.') ? "--filter-method" : "--filter-class", $"*{value}*").Keys)
            .ToHashSet(StringComparer.Ordinal);
        var checks = Resolve(string.Join(separator, values.Select(Fqn)), out _);
        var actual = checks.SelectMany(check => DiscoverManagedTests(FilterArgv(check)).Keys).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
        Assert.Empty(DiscoverManagedTests(allowEmpty: true,
            "--filter-class", $"*{ClassA}*", "--filter-class", $"*{ClassB}*",
            "--filter-method", $"*{MethodA}*", "--filter-method", $"*{MethodB}*"));
    }

    [Xunit.Fact]
    public void OverlapNegativeControl_IsNonemptyAndOmitsRequestedIdentities()
    {
        var classTests = DiscoverManagedTestCatalog(false, "--filter-class", $"*{ClassA}*");
        var method = $"{ClassA}.{classTests[0].MethodName}";
        var methods = DiscoverManagedTests("--filter-method", $"*{method}*");
        var disjoint = DiscoverManagedTests("--filter-method", $"*{MethodA}*");
        var expected = classTests.Select(test => test.Uid).Union(methods.Keys).Union(disjoint.Keys).ToHashSet(StringComparer.Ordinal);
        var old = DiscoverManagedTests("--filter-class", $"*{ClassA}*",
            "--filter-method", $"*{method}*", "--filter-method", $"*{MethodA}*");
        Assert.NotEmpty(old);
        Assert.True(old.Keys.ToHashSet(StringComparer.Ordinal).IsProperSubsetOf(expected),
            "The old argv must execute a nonempty proper subset, so zero-test protection alone cannot catch this defect.");
        var checks = Resolve($"{Fqn(ClassA)}|{Fqn(method)}|{Fqn(MethodA)}", out _);
        var actual = checks.SelectMany(check => DiscoverManagedTests(FilterArgv(check)).Keys).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
    }

    [Xunit.Fact]
    public void LocalExclusion_DoesNotRemoveOtherAlternative()
    {
        var excluded = DiscoverManagedTests("--filter-class", $"*{ClassA}*", "--filter-not-trait", "Category=FocusedUnionProbe");
        var method = DiscoverManagedTests("--filter-method", $"*{MethodA}*");
        var checks = Resolve($"({Fqn(ClassA)}&Category!=FocusedUnionProbe)|{Fqn(MethodA)}", out _);
        var actual = checks.SelectMany(check => DiscoverManagedTests(FilterArgv(check)).Keys).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(excluded.Keys.Union(method.Keys).Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));

        // The exclusion intentionally matches the second alternative's containing class.
        // Hoisting it would erase that entire method selection.
        checks = Resolve($"({Fqn(ClassA)}&FullyQualifiedName!~MtpTestRunnerScriptTests)|{Fqn(MethodA)}", out _);
        actual = checks.SelectMany(check => DiscoverManagedTests(FilterArgv(check)).Keys).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(excluded.Keys.Union(method.Keys).Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
    }

    [Xunit.Fact]
    public void EquivalentRecordedShape_ExecutesAllNamedClassesAndMethods()
    {
        // The ad3 branch-only custody method is represented solely in the planning
        // fixture. These two existing methods provide real managed execution here.
        var values = new[] { ClassA, ClassB, "MtpTestRunnerScriptTestsSilenceGuard", MethodA, MethodB };
        var checks = Resolve(string.Join("; ", values.Select(value => "Infrastructure.Tests:" + Fqn(value))), out _);
        var root = Directory.CreateTempSubdirectory("focused-union-execution-").FullName;
        try
        {
            var receipts = new List<string>();
            foreach (var check in checks)
            {
                var expected = DiscoverManagedTestCatalog(false, FilterArgv(check));
                var receipt = Path.Combine(root, $"partition-{receipts.Count}.trx");
                var argv = Argv(check, receipt);
                var stdoutName = $"focused-union-{Guid.NewGuid():N}-out";
                var stderrName = $"focused-union-{Guid.NewGuid():N}-err";
                using var stdout = OperatingSystem.IsWindows() ? GoalAcceptanceVerifierCaptureCustody.CreateCapturePipe(stdoutName) : null;
                using var stderr = OperatingSystem.IsWindows() ? GoalAcceptanceVerifierCaptureCustody.CreateCapturePipe(stderrName) : null;
                var startInfo = GoalAcceptanceVerifierCaptureCustody.BuildAcceptanceProcessStartInfo(argv, RepositoryRoot(),
                    stdout is null ? null : $@"\\.\pipe\{stdoutName}", stderr is null ? null : $@"\\.\pipe\{stderrName}", forceUtf8ConsoleOutput: true);
                var result = Run(startInfo, TimeSpan.FromMinutes(3),
                    capturePipes: stdout is null ? null : (stdout, stderr!));
                Assert.True(result.ExitCode == 0, $"exit={result.ExitCode}: {result.Stdout} {result.Stderr}");
                Assert.True(File.Exists(receipt), "The managed launch did not produce its invocation-owned TRX.");
                var extraction = GoalAcceptanceVerifierTestTelemetry.ExtractTrxExecutedTestIdentities([receipt]);
                Assert.Empty(extraction.UnreadableReceiptPaths);
                Assert.Equal(expected.Count, System.Xml.Linq.XDocument.Load(receipt).Descendants()
                    .Count(element => element.Name.LocalName == "UnitTestResult"));
                var expectedIdentities = expected.Select(test =>
                {
                    var parameters = test.DisplayName.IndexOfAny(['(', '[']);
                    return test.TypeName + "." + test.MethodName +
                        (parameters < 0 ? string.Empty : test.DisplayName[parameters..].Trim());
                }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
                Assert.Equal(expectedIdentities, extraction.Identities.Order(StringComparer.Ordinal));
                Assert.Empty(GoalAcceptanceVerifierTestTelemetry.InspectFocusedEvidenceSelectionCoverage(
                    check.FocusedEvidenceSelections, [receipt]).UncoveredSelections);
                receipts.Add(receipt);
            }
            Assert.Equal(2, receipts.Count);
            var all = GoalAcceptanceVerifierTestTelemetry.ExtractTrxExecutedTestIdentities(receipts).Identities;
            Assert.All(values, value => Assert.Contains(all, identity => identity.Contains(value, StringComparison.Ordinal)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
