using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.GoalAcceptanceVerifier)]
public sealed class GoalAcceptanceVerifierTestsSourceSizePreflight : GoalAcceptanceVerifierTestBase
{
    [Xunit.Fact]
    public async Task ViolatingAuthority_FailsBeforeAnyProcessPhase()
    {
        var root = CreateManifestWorkspace(EmptyManifest);
        WriteAuthority(root, maximumLineCount: 2, actualLineCount: 3);
        var calls = new List<string[]>();
        var phases = new List<string>();
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, string.Empty));
        });

        try
        {
            using var progress = GoalAcceptanceVerifier.PushGateProgressSink(item => phases.Add(item.Phase));
            var result = await verifier.RunAsync(root);

            Assert.False(result.Passed);
            Assert.Empty(calls);
            Assert.DoesNotContain(AcceptanceGatePhaseNames.BuildServerShutdown, phases);
            Assert.Contains("guarded.cs", result.OutputTail, StringComparison.Ordinal);
            Assert.Contains("3 lines", result.OutputTail, StringComparison.Ordinal);
            Assert.Contains("recorded ceiling of 2", result.OutputTail, StringComparison.Ordinal);
            Assert.Contains(
                "Extract behavior to a collaborator and lower the ceiling",
                result.OutputTail,
                StringComparison.Ordinal);
            Assert.Contains(
                "raise the recorded ceiling for this entry deliberately with justification in the same change",
                result.OutputTail,
                StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact]
    public void ViolatingAuthority_ReportsEveryOffendingFile()
    {
        var root = CreateManifestWorkspace(EmptyManifest);
        var authorityPath = Path.Combine(
            root,
            SourceSizeRatchet.SourcePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(authorityPath)!);
        File.WriteAllText(
            authorityPath,
            "new SourceSizeCeiling(\"first.cs\", 2)" + Environment.NewLine +
            "new SourceSizeCeiling(\"second.cs\", 4)");
        File.WriteAllLines(Path.Combine(root, "first.cs"), Enumerable.Repeat("line", 3));
        File.WriteAllLines(Path.Combine(root, "second.cs"), Enumerable.Repeat("line", 6));

        try
        {
            var result = SourceSizeRatchetPreflight.Evaluate(root);

            Assert.True(result.HasBlockingViolation);
            Assert.Contains("first.cs has 3 lines", result.Message, StringComparison.Ordinal);
            Assert.Contains("recorded ceiling of 2", result.Message, StringComparison.Ordinal);
            Assert.Contains("second.cs has 6 lines", result.Message, StringComparison.Ordinal);
            Assert.Contains("recorded ceiling of 4", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact]
    public async Task CompliantAuthority_ProceedsToBuildServerShutdown()
    {
        var root = CreateManifestWorkspace(EmptyManifest);
        WriteAuthority(root, maximumLineCount: 3, actualLineCount: 3);
        var calls = new List<string[]>();
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, string.Empty));
        });

        try
        {
            var result = await verifier.RunAsync(root);

            Assert.True(result.Passed);
            Assert.NotEmpty(calls);
            Assert.Equal(["dotnet", "build-server", "shutdown"], calls[0]);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact]
    public void UnreadableGuardedFile_DeclinesRatherThanBlocking()
    {
        var root = CreateManifestWorkspace(EmptyManifest);
        WriteAuthority(root, maximumLineCount: 3, actualLineCount: 3);

        try
        {
            var result = SourceSizeRatchetPreflight.Evaluate(
                root,
                _ => throw new IOException("simulated read failure"));

            Assert.False(result.HasBlockingViolation);
            var violation = Assert.Single(result.Violations);
            Assert.Null(violation.ActualLineCount);
            Assert.Contains("could not be read", violation.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    private static void WriteAuthority(string root, int maximumLineCount, int actualLineCount)
    {
        var authorityPath = Path.Combine(
            root,
            SourceSizeRatchet.SourcePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(authorityPath)!);
        File.WriteAllText(
            authorityPath,
            $"new SourceSizeCeiling(\"guarded.cs\", {maximumLineCount})");
        File.WriteAllLines(
            Path.Combine(root, "guarded.cs"),
            Enumerable.Repeat("line", actualLineCount));
    }

    private const string EmptyManifest = """
        {
          "version": 1,
          "checks": [],
          "forbiddenChangedPathGlobs": []
        }
        """;
}
