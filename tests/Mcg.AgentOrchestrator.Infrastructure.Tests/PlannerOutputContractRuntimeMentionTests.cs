using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its temporary repository and uses no process or clock.
public sealed class PlannerOutputContractRuntimeMentionTests : WorkerDispatchTestSupport
{
    [Xunit.Fact]
    public void Resolve_NamespaceExcerpt_Succeeds()
    {
        const string excerpt = "  - a pure `FailureClusterInputParser` (`ParseGoalEventLine` and `ParseConductEventLine` over `System.Text.Json`) that skips torn or malformed lines.";

        var result = ResolveExcerpt(excerpt);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
        Xunit.Assert.Contains(excerpt, result.Plan, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("  - It is stored camelCase in `landing-escalations.json` and locked through `landing-escalations.lock` (`OperatorInbox.cs:106`, `OperatorInbox.cs:107`).")]
    [Xunit.InlineData("  - The host keeps its state file `host-health-state.json` in `OrchestratorDirectory`.")]
    [Xunit.InlineData("  - Observe `/.orchestrator/conductor-policy.json` for runtime policy state.")]
    [Xunit.InlineData("  - Observe `$artifactsPath\\worker-build-receipt.json` for build evidence.")]
    [Xunit.InlineData("  - Observe `.orchestrator/conductor-policy.json` for runtime policy state.")]
    [Xunit.InlineData("  - Observe `.orchestrator\\conductor-policy.json` for runtime policy state.")]
    [Xunit.InlineData("  - Observe `\\.ORCHESTRATOR\\conductor-policy.json` for runtime policy state.")]
    [Xunit.InlineData("  - Inspect `$sourcePath\\Missing.cs` for the script-provided source path.")]
    public void Resolve_RuntimeArtifactExcerpt_Succeeds(string excerpt)
    {
        var result = ResolveExcerpt(excerpt);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
        Xunit.Assert.Contains(excerpt.Trim(), result.Plan, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("src/Absent/settings.json")]
    [Xunit.InlineData("Missing.cs")]
    [Xunit.InlineData("Missing.csproj")]
    [Xunit.InlineData("Missing.ps1")]
    [Xunit.InlineData("Missing.props")]
    [Xunit.InlineData("Missing.targets")]
    [Xunit.InlineData("Missing.md")]
    [Xunit.InlineData("Missing.txt")]
    [Xunit.InlineData("Missing.yml")]
    [Xunit.InlineData("Missing.yaml")]
    [Xunit.InlineData("lease/lease.json")]
    [Xunit.InlineData(".orchestrator-other/settings.json")]
    [Xunit.InlineData("*.json")]
    [Xunit.InlineData(".orchestrator/*.json")]
    [Xunit.InlineData(".orchestrator/settings?.json")]
    [Xunit.InlineData("$1.cs")]
    [Xunit.InlineData("${var}/Missing.cs")]
    public void Resolve_MissingCitation_KeepsDiagnostic(string citation)
    {
        var result = ResolveExcerpt($"  - Inspect `{citation}` for the concrete implementation seam.");

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains(
            $"target citation '{citation}' does not exist and is not marked as a new file",
            result.Diagnostic, StringComparison.Ordinal);
        Xunit.Assert.Contains($"Offending citation: '{citation}'", result.Diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void Resolve_AmbiguousBareJson_KeepsDiagnostic()
    {
        var workingDirectory = CreateFixtureRepository();
        Directory.CreateDirectory(Path.Combine(workingDirectory, "src", "Other"));
        File.WriteAllText(Path.Combine(workingDirectory, "src", "settings.json"), "{}");
        File.WriteAllText(Path.Combine(workingDirectory, "src", "Other", "settings.json"), "{}");
        var plan = PlanWithExcerpt("  - Inspect `settings.json` for configuration.");

        var result = PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains("target citation 'settings.json' is ambiguous", result.Diagnostic, StringComparison.Ordinal);
        Xunit.Assert.Contains("'src/settings.json'", result.Diagnostic, StringComparison.Ordinal);
        Xunit.Assert.Contains("'src/Other/settings.json'", result.Diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("src/Present.cs:12-14")]
    [Xunit.InlineData("src/Present.cs#l12-l14")]
    [Xunit.InlineData("src/Present.cs::SomeMethod")]
    public void Resolve_SourceSuffix_Succeeds(string citation)
    {
        var result = ResolveExcerpt($"  - Inspect `{citation}` for the concrete implementation seam.");

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
    }

    private static PlannerOutputContractResult ResolveExcerpt(string excerpt) =>
        PlannerOutputContract.Resolve(PlanWithExcerpt(excerpt), string.Empty, CreateFixtureRepository());

    private static string CreateFixtureRepository()
    {
        var workingDirectory = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(workingDirectory, "src"));
        File.WriteAllText(Path.Combine(workingDirectory, "src", "Present.cs"), "// fixture");
        File.WriteAllText(Path.Combine(workingDirectory, "src", "OperatorInbox.cs"), "// fixture");
        return workingDirectory;
    }

    private static string PlanWithExcerpt(string excerpt)
    {
        var normalized = PlannerContractPlanFixture().ReplaceLineEndings("\n");
        const string heading = "## Target seams and symbols";
        var bodyStart = normalized.IndexOf(heading, StringComparison.Ordinal) + heading.Length;
        var nextHeading = normalized.IndexOf("\n## ", bodyStart, StringComparison.Ordinal);
        return normalized[..bodyStart] + "\n\n- Inspect `src/Present.cs` for the concrete implementation seam.\n" +
            excerpt + "\n" + normalized[nextHeading..];
    }
}
