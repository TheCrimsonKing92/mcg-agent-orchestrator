using Xunit;

public sealed class ConsoleReplacementSourceGuardTests
{
    private static readonly string[] ReplacementCalls =
    [
        "Console.Set" + "Out(",
        "Console.Set" + "Error("
    ];

    private static readonly string[] Exceptions =
    [
        // Drains a real child process into replaced streams in the process-local lane.
        "ChildOutputDrainAdoptionSaturationTests.cs"
    ];

    [Fact]
    public void TestSourcesReplaceConsoleOnlyForInstallationAndChildDrain()
    {
        foreach (var call in ReplacementCalls)
        {
            var negativeControl = Scan(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Replacement.cs"] = call + "new StringWriter());"
                },
                []);
            Assert.Equal(["Replacement.cs"], negativeControl.Violations);
            Assert.Empty(negativeControl.StaleExceptions);
        }

        var duplicateNameControl = Scan(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["First/Program.cs"] = ReplacementCalls[0] + "new StringWriter());",
                ["Second/Program.cs"] = ReplacementCalls[1] + "new StringWriter());"
            },
            []);
        Assert.Equal(["First/Program.cs", "Second/Program.cs"], duplicateNameControl.Violations);
        Assert.Empty(duplicateNameControl.StaleExceptions);

        var staleControl = Scan(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["NoReplacement.cs"] = "class NoReplacement { }"
            },
            ["Missing.cs", "NoReplacement.cs"]);
        Assert.Empty(staleControl.Violations);
        Assert.Equal(["Missing.cs", "NoReplacement.cs"], staleControl.StaleExceptions);

        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var snapshot = RepositorySourceSnapshotCache.Get(root);
        var sources = snapshot.EnumerateFiles(Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            .Where(path => !SourceGuardFileFilter.BuildOutputAndScratch.Excludes(path))
            .ToDictionary(path => path, path => snapshot.GetText(path), StringComparer.Ordinal);
        Assert.True(sources.Count > 0, "The Infrastructure test source snapshot must not be empty.");

        var result = Scan(sources, Exceptions);
        Assert.Empty(result.Violations);
        Assert.Empty(result.StaleExceptions);
    }

    private static (string[] Violations, string[] StaleExceptions) Scan(
        IReadOnlyDictionary<string, string> sources,
        IReadOnlyCollection<string> exceptions)
    {
        var callers = sources
            .Where(source => ReplacementCalls.Any(call => source.Value.Contains(call, StringComparison.Ordinal)))
            .Select(source => source.Key)
            .ToHashSet(StringComparer.Ordinal);
        var violations = callers
            .Where(path => Path.GetFileName(path) != "ConsoleCapture.cs" &&
                !exceptions.Contains(Path.GetFileName(path), StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var staleExceptions = exceptions
            .Where(file => !callers.Any(path => string.Equals(Path.GetFileName(path), file, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)
            .ToArray();
        return (violations, staleExceptions);
    }
}
