using System.Reflection;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: console capture is AsyncLocal and executable resolution is supplied per call.
public sealed class EnvironmentHealthQueryCharacterizationTests
{
    // Expected text derived from ConsoleViews.Configuration.cs:77-105,132-192 before extraction,
    // OrchestratorHealthInspector.InspectProfile and WorkerProfileDiagnostics.EvaluatePatchCapability.
    [Fact]
    public void UnknownProfile_ThrowsBeforeWritingAnything()
    {
        var output = AsyncLocalConsoleRouter.Capture(() =>
        {
            var exception = Assert.Throws<KeyNotFoundException>(() =>
                ConsoleViews.PrintWorkerProfileChecks(new AgentCatalog([]), new WorkerProfileCatalog([]),
                    "nope", CommandExists));
            Assert.Equal("Worker profile 'nope' was not found.", exception.Message);
        });
        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public void ResolvableProfilesWithoutSubscriptionAgents_PrintEveryProfileInCatalogOrder()
    {
        AssertCheck(new AgentCatalog([]), new WorkerProfileCatalog([
                new WorkerProfile("beta-worker", "beta --model {subscriptionModelName}"),
                new WorkerProfile("alpha-worker", "alpha --model {subscriptionModelName}")]),
            Lines("Worker profile checks:",
                "  beta-worker: executable=beta resolvable=True patchCapable=True (Provider is not a typed Codex or Claude launcher; patch capability cannot be inferred beyond executing the prompt.)",
                "  alpha-worker: executable=alpha resolvable=True patchCapable=True (Provider is not a typed Codex or Claude launcher; patch capability cannot be inferred beyond executing the prompt.)"));
    }

    [Fact]
    public void UnresolvableProfile_PrintsEveryProfileBeforeThrowing()
    {
        AssertCheck(new AgentCatalog([]), new WorkerProfileCatalog([
                new WorkerProfile("broken-worker", "missing --model {subscriptionModelName}"),
                new WorkerProfile("alpha-worker", "alpha --model {subscriptionModelName}")]),
            Lines("Worker profile checks:",
                "  broken-worker: executable=missing resolvable=False patchCapable=True (Command was not found on PATH or as a PowerShell command.)",
                "  alpha-worker: executable=alpha resolvable=True patchCapable=True (Provider is not a typed Codex or Claude launcher; patch capability cannot be inferred beyond executing the prompt.)"),
            "One or more worker profiles are not resolvable.");
    }

    [Fact]
    public void UnconfiguredSubscriptionProfile_PrintsRouteBeforeThrowing()
    {
        AssertCheck(Agents("absent-worker"), new WorkerProfileCatalog([]),
            Lines("Worker profile checks:",
                "  route Planner: profile=absent-worker (subscription profile is not configured)"),
            "One or more active subscription routes are not usable.");
    }

    [Fact]
    public void NonExecutableSubscriptionProfile_PrintsRouteBeforeProfileFailure()
    {
        AssertCheck(Agents("broken-worker"), new WorkerProfileCatalog([
                new WorkerProfile("broken-worker", "missing --model {subscriptionModelName}")]),
            Lines("Worker profile checks:",
                "  broken-worker: executable=missing resolvable=False patchCapable=True (Command was not found on PATH or as a PowerShell command.)",
                "  route Planner: profile=broken-worker (subscription profile is not executable)"),
            "One or more worker profiles are not resolvable.");
    }

    [Fact]
    public void EchoOnlySubscriptionProfile_PrintsRouteBeforeThrowing()
    {
        AssertCheck(Agents("echo-worker"), new WorkerProfileCatalog([
                new WorkerProfile("echo-worker", "echo {promptPath}")]),
            Lines("Worker profile checks:",
                "  echo-worker: executable=echo resolvable=True patchCapable=False (Command only echoes the prompt path; subscription dispatch would not execute worker work.)",
                "  route Planner: profile=echo-worker (subscription profile only echoes the prompt path)"),
            "One or more active subscription routes are not usable.");
    }

    [Fact]
    public void UnpinnedSubscriptionModel_PrintsRouteBeforeThrowing()
    {
        AssertCheck(Agents("alpha-worker"), new WorkerProfileCatalog([
                new WorkerProfile("alpha-worker", "alpha")]),
            Lines("Worker profile checks:",
                "  alpha-worker: executable=alpha resolvable=True patchCapable=True (Provider is not a typed Codex or Claude launcher; patch capability cannot be inferred beyond executing the prompt.)",
                "  route Planner: profile=alpha-worker (subscription profile does not pin the selected model)"),
            "One or more active subscription routes are not usable.");
    }

    [Theory]
    [InlineData("high", null)]
    [InlineData(null, "high")]
    public void OpenAiReasoningEffortFromModelOrSubscription_MustBePinned(
        string? modelEffort, string? subscriptionEffort)
    {
        AssertCheck(Agents("alpha-worker", modelEffort: modelEffort, subscriptionEffort: subscriptionEffort),
            new WorkerProfileCatalog([new WorkerProfile("alpha-worker", "alpha --model {subscriptionModelName}")]),
            Lines("Worker profile checks:",
                "  alpha-worker: executable=alpha resolvable=True patchCapable=True (Provider is not a typed Codex or Claude launcher; patch capability cannot be inferred beyond executing the prompt.)",
                "  route Planner: profile=alpha-worker (subscription profile does not pin the selected reasoning effort)"),
            "One or more active subscription routes are not usable.");
    }

    [Fact]
    public void NonOpenAiReasoningEffort_DoesNotRequireReasoningPlaceholder()
    {
        AssertCheck(Agents("alpha-worker", provider: "Anthropic", modelEffort: "high"),
            new WorkerProfileCatalog([new WorkerProfile("alpha-worker", "alpha --model {subscriptionModelName}")]),
            Lines("Worker profile checks:",
                "  alpha-worker: executable=alpha resolvable=True patchCapable=True (Provider is not a typed Codex or Claude launcher; patch capability cannot be inferred beyond executing the prompt.)"));
    }

    [Fact]
    public void PinnedOpenAiReasoningEffort_ProducesNoRouteIssue()
    {
        AssertCheck(Agents("alpha-worker", modelEffort: "high"),
            new WorkerProfileCatalog([new WorkerProfile("alpha-worker",
                "alpha --model {subscriptionModelName} --effort {subscriptionReasoningEffort}")]),
            Lines("Worker profile checks:",
                "  alpha-worker: executable=alpha resolvable=True patchCapable=True (Provider is not a typed Codex or Claude launcher; patch capability cannot be inferred beyond executing the prompt.)"));
    }

    [Fact]
    public void DeveloperWithoutPatchCapability_PrintsRouteBeforeThrowing()
    {
        AssertCheck(Agents("codex-cli", role: AgentRole.Developer),
            new WorkerProfileCatalog([new WorkerProfile("codex-cli", "codex exec --model {subscriptionModelName}")]),
            Lines("Worker profile checks:",
                "  codex-cli: executable=codex resolvable=True patchCapable=False (Codex launcher is not patch-capable; missing --sandbox workspace-write, --cd {workingDirectory}.)",
                "  route Developer: profile=codex-cli (subscription profile cannot patch Developer tasks)"),
            "One or more active subscription routes are not usable.");
    }

    [Fact]
    public void NameFilter_ExcludesOtherProfilesAndTheirRouteIssues()
    {
        AssertCheck(Agents("broken-worker"), new WorkerProfileCatalog([
                new WorkerProfile("broken-worker", "missing"),
                new WorkerProfile("alpha-worker", "alpha --model {subscriptionModelName}")]),
            Lines("Worker profile checks:",
                "  alpha-worker: executable=alpha resolvable=True patchCapable=True (Provider is not a typed Codex or Claude launcher; patch capability cannot be inferred beyond executing the prompt.)"),
            name: "ALPHA-WORKER");
    }

    [Fact]
    public void ProfileAndRouteFailures_PrintAllLinesThenPreferProfileFailure()
    {
        var agents = new AgentCatalog([
            Agent("absent-worker", AgentRole.Developer),
            Agent("alpha-worker", AgentRole.Planner)]);
        AssertCheck(agents, new WorkerProfileCatalog([
                new WorkerProfile("broken-worker", "missing --model {subscriptionModelName}"),
                new WorkerProfile("alpha-worker", "alpha")]),
            Lines("Worker profile checks:",
                "  broken-worker: executable=missing resolvable=False patchCapable=True (Command was not found on PATH or as a PowerShell command.)",
                "  alpha-worker: executable=alpha resolvable=True patchCapable=True (Provider is not a typed Codex or Claude launcher; patch capability cannot be inferred beyond executing the prompt.)",
                "  route Planner: profile=alpha-worker (subscription profile does not pin the selected model)",
                "  route Developer: profile=absent-worker (subscription profile is not configured)"),
            "One or more worker profiles are not resolvable.");
    }

    [Fact]
    public void ConsoleViewsRatchet_UsesSliceThirteenStepTwoAtOrUnderBudget()
    {
        var ceiling = Assert.Single(SourceSizeRatchet.SeededClassCeilings,
            item => item.ClassName == "ConsoleViews");
        Assert.True(ceiling.MaximumTotalLineCount <= 2865);
        Assert.Equal(38, ceiling.MaximumPartialFileCount);
        Assert.Empty(SourceSizeRatchet.EvaluateClasses(VerifiedRepositoryRoot.Find(), [ceiling]));
    }

    [Fact]
    public void ExtractedQuery_OwnsInspectionWithoutConsoleViewsForwarders()
    {
        var root = VerifiedRepositoryRoot.Find();
        var queryPath = Path.Combine(root, "src", "Mcg.AgentOrchestrator.App", "Cli", "EnvironmentHealthQuery.cs");
        Assert.True(File.Exists(queryPath), "EnvironmentHealthQuery.cs must own the extracted inspection and validation.");
        var query = File.ReadAllText(queryPath);
        Assert.Contains("internal static class EnvironmentHealthQuery", query);
        Assert.DoesNotMatch(@"\bpartial\s+class\b", query);
        Assert.DoesNotContain("Console.", query);
        Assert.Contains("OrchestratorHealthInspector.InspectCurrentEnvironment(agents, selected, commandExists)", query);
        Assert.Contains("catalog.GetRequired(name)", query);
        Assert.Contains("BuildSubscriptionRouteIssues", query);
        Assert.Contains("private static bool RequiresSubscriptionReasoningPlaceholder", query);

        var oldMethods = typeof(ConsoleViews).GetMethods(
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        foreach (var name in new[] { "BuildSubscriptionRouteIssues", "RequiresSubscriptionReasoningPlaceholder" })
            Assert.DoesNotContain(oldMethods, method => method.Name == name);

        // Use the ratchet's declaration scan, excluding build outputs.
        var partials = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    segment.Equals("obj", StringComparison.OrdinalIgnoreCase)))
            .Select(File.ReadAllText)
            .Where(text => Regex.IsMatch(text, @"\bpartial\s+class\s+ConsoleViews\b", RegexOptions.CultureInvariant))
            .ToArray();
        Assert.Equal(38, partials.Length);
        foreach (var text in partials)
        {
            Assert.DoesNotMatch(@"\bstatic\s+\S+\s+(?:BuildSubscriptionRouteIssues|RequiresSubscriptionReasoningPlaceholder)\s*\(", text);
            Assert.DoesNotMatch(@"(?:=>|\breturn)\s*EnvironmentHealthQuery\.", text);
        }
        Assert.Equal(2, partials.Sum(text => Regex.Matches(text, @"\bEnvironmentHealthQuery\.").Count));
    }

    private static void AssertCheck(AgentCatalog agents, WorkerProfileCatalog profiles,
        string expectedOutput, string? expectedException = null, string? name = null)
    {
        Exception? exception = null;
        var output = AsyncLocalConsoleRouter.Capture(() =>
        {
            exception = Record.Exception(() => ConsoleViews.PrintWorkerProfileChecks(agents, profiles, name, CommandExists));
        });
        Assert.Equal(expectedOutput, output);
        if (expectedException is null)
            Assert.Null(exception);
        else
            Assert.Equal(expectedException, Assert.IsType<InvalidOperationException>(exception).Message);
    }

    private static string Lines(params string[] lines) => string.Join(Environment.NewLine, lines) + Environment.NewLine;

    private static bool CommandExists(string executable) => executable != "missing";

    private static AgentCatalog Agents(string profile, AgentRole role = AgentRole.Planner,
        string provider = "OpenAI", string? modelEffort = null, string? subscriptionEffort = null) =>
        new([Agent(profile, role, provider, modelEffort, subscriptionEffort)]);

    private static AgentDefinition Agent(string profile, AgentRole role,
        string provider = "OpenAI", string? modelEffort = null, string? subscriptionEffort = null) =>
        new(new AgentId(role.ToString()), role.ToString(), role,
            new ModelProfile(provider, "test-model", ModelCapability.Text, SubscriptionMode.ApiKey,
                ReasoningEffort: modelEffort),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile(profile, ReasoningEffort: subscriptionEffort));
}
