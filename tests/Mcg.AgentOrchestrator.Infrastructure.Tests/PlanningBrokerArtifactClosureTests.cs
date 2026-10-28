using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static BrokerDeliveryTestFixture;

// Parallel-safe: each actual PrepareTask call owns a private Git repository, kernel and package.
// No provider, language SDK, test subprocess or shared orchestrator state is used.
public sealed class PlanningBrokerArtifactClosureTests
{
    public static IEnumerable<object[]> PlanningCases()
    {
        foreach (var role in new[] { AgentRole.Planner, AgentRole.Researcher })
        foreach (var retry in new[] { false, true })
        foreach (var toolchain in new[] { Toolchain.Dotnet, Toolchain.Go, Toolchain.Node, Toolchain.Python, Toolchain.Unknown })
            yield return [role, retry, toolchain];
    }

    [Theory]
    [MemberData(nameof(PlanningCases))]
    public void RequiredReferencesResolveInReceivingPackage(AgentRole role, bool retry, Toolchain toolchain)
    {
        using var fixture = PreparePlanning(role, retry, toolchain);
        var actions = ReadActions(fixture.ReadDelivered("workflow-brokers.md"));
        foreach (var name in new[] { "build-test-selection", "static-policy-checks", "acceptance-evidence", "diff-summary" })
        {
            Assert.Contains(name, actions.Keys);
            Assert.NotEmpty(RequiredPlanningArtifacts(actions[name]));
            Assert.Contains("  Use for:", actions[name], StringComparison.Ordinal);
            Assert.Contains("  Failure handling:", actions[name], StringComparison.Ordinal);
        }

        // Keep closure first and unchanged for Acceptance's matched baseline/candidate run.
        // Pre-repair Planner and Researcher must fail specifically on the unauthorized checklist,
        // rather than on candidate wording, unfiltered files, or a fixture setup assumption.
        Assert.All(actions.Values.SelectMany(RequiredPlanningArtifacts), fixture.AssertPlanningArtifactAvailable);
    }

    [Theory]
    [MemberData(nameof(PlanningCases))]
    public void PlanningGuidancePreservesContextAndExecutionOwners(AgentRole role, bool retry, Toolchain toolchain)
    {
        using var fixture = PreparePlanning(role, retry, toolchain);
        var broker = fixture.ReadDelivered("workflow-brokers.md");
        var actions = ReadActions(broker);
        var selection = actions["build-test-selection"];
        Assert.Contains("Current recommendation:", selection, StringComparison.Ordinal);
        Assert.Contains("Selection scope: build needed=", selection, StringComparison.Ordinal);
        Assert.Contains("Acceptance owns deterministic build/test execution and receipts", selection, StringComparison.Ordinal);
        var stackContext = toolchain switch
        {
            Toolchain.Dotnet => ".NET project builds and focused test classes",
            Toolchain.Go => "Go package build, vet, and focused package tests",
            Toolchain.Node => "Node build, lint, and test scripts",
            Toolchain.Python => "Python project build, lint, type checks, and focused tests",
            _ => "project-standard build, policy checks, and tests discovered from repository conventions"
        };
        Assert.Contains(stackContext, selection, StringComparison.Ordinal);
        var policy = actions["static-policy-checks"];
        Assert.Contains("Current policy:", policy, StringComparison.Ordinal);
        Assert.Contains("generated-path risk, worker-result contract checks, and manifest presence", policy, StringComparison.Ordinal);
        Assert.Contains("Acceptance owns deterministic policy checks", policy, StringComparison.Ordinal);
        Assert.Contains("Operator owns external observations only when the criterion declares them", policy, StringComparison.Ordinal);
        Assert.Contains("permitted read-only workspace inspection", actions["diff-summary"], StringComparison.Ordinal);
        Assert.Contains("Acceptance supplies deterministic build/test and gate receipts", actions["acceptance-evidence"], StringComparison.Ordinal);
        Assert.Contains("recommendations are not completed verification", actions["acceptance-evidence"], StringComparison.Ordinal);
        Assert.DoesNotContain("Suggested command shape:", broker, StringComparison.Ordinal);
        Assert.DoesNotContain("Current commands:", broker, StringComparison.Ordinal);
        Assert.DoesNotContain("  Command:", broker, StringComparison.Ordinal);
        Assert.DoesNotContain("deterministic-verification.md", broker, StringComparison.Ordinal);
        Assert.DoesNotContain("  Artifact: diff-summary.md", broker, StringComparison.Ordinal);
        if (toolchain != Toolchain.Dotnet)
        {
            Assert.DoesNotContain(".NET", broker, StringComparison.Ordinal);
            Assert.DoesNotContain("dotnet", broker, StringComparison.OrdinalIgnoreCase);
        }
        if (actions.TryGetValue("backlog-log-evidence", out var backlog))
        {
            Assert.Contains("request durable backlog/log maintenance from the Operator", backlog, StringComparison.Ordinal);
            Assert.Contains("use only supplied records", backlog, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(AgentRole.Planner, false)]
    [InlineData(AgentRole.Planner, true)]
    [InlineData(AgentRole.Researcher, false)]
    [InlineData(AgentRole.Researcher, true)]
    public void PlanningPolicyRetainsRequiredRiskReview(AgentRole role, bool riskReview)
    {
        using var fixture = Prepare(role, retry: false, requireRiskReview: riskReview);
        var policy = ReadActions(fixture.ReadDelivered("workflow-brokers.md"))["static-policy-checks"];
        Assert.Contains($"human risk review={riskReview}", policy, StringComparison.Ordinal);
        const string requirement = "Policy requirement: human risk review; kind=manual-risk-review; required=True";
        if (riskReview)
            Assert.Contains(requirement, policy, StringComparison.Ordinal);
        else
            Assert.DoesNotContain(requirement, policy, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(PlanningCases))]
    public void RestrictedArtifactsRemainOutsideDelivery(AgentRole role, bool retry, Toolchain toolchain)
    {
        using var fixture = PreparePlanning(role, retry, toolchain);
        Assert.NotEmpty(fixture.ReadDelivered("workflow-brokers.md"));
        fixture.AssertPlanningArtifactAvailable("current-task.md");
        using var registry = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(fixture.ContextDirectory, "artifact-registry.json")));
        foreach (var name in new[] { "deterministic-verification.md", "diff-summary.md" })
        {
            // The unfiltered files deliberately exist; their presence is not delivery authority.
            Assert.True(File.Exists(Path.Combine(fixture.ContextDirectory, name)));
            Assert.DoesNotContain(fixture.Receipt.Sections, section => section.LogicalIdentity == $"context/{name}");
            Assert.DoesNotContain($"identity=context/{name};", fixture.Prompt, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.ContextDirectory, "authoritative"),
                name, SearchOption.AllDirectories));
            var entry = Assert.Single(registry.RootElement.GetProperty("artifacts").EnumerateArray(),
                artifact => artifact.GetProperty("path").GetString() == name);
            var visibility = entry.GetProperty("roleVisibility").EnumerateArray()
                .Select(value => value.GetString()).ToArray();
            Assert.Equal(name == "deterministic-verification.md"
                ? new[] { "Tester", "Reviewer" }
                : new[] { "Developer", "Tester", "Reviewer" }, visibility);
            Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => fixture.AssertPlanningArtifactAvailable(name));
        }
    }

    [Theory]
    [InlineData("  Artifact: deterministic-verification.md", "deterministic-verification.md")]
    [InlineData("  Use for: read deterministic-verification.md before selecting tests.", "deterministic-verification.md")]
    [InlineData("  Use for: follow `deterministic-verification.md` for required policy.", "deterministic-verification.md")]
    [InlineData("  Evidence: use [the checklist](deterministic-verification.md) to report results.", "deterministic-verification.md")]
    [InlineData("  Failure handling: consult context/diff-summary.md for changed files.", "diff-summary.md")]
    [InlineData("  Use for: current-task.md includes optional evidence details.", "current-task.md")]
    public void DependencyScanIncludesRequiredProse(string guidance, string required)
    {
        Assert.Equal([required], RequiredPlanningArtifacts(guidance));
    }

    [Fact]
    public void DependencyScanSeparatesExplicitOptionalAndDescriptiveReferences()
    {
        const string guidance = "  Artifact: current-task.md\n" +
            "  Optional artifact: optional-notes.md\n" +
            "  Descriptive artifact: historical-evidence.md\n" +
            "  Evidence: follow current-task.md for the handoff.";
        Assert.Equal(["current-task.md"], RequiredPlanningArtifacts(guidance));
    }

    [Theory]
    [InlineData(AgentRole.Planner)]
    [InlineData(AgentRole.Researcher)]
    public void ProseOnlyHiddenDependencyIsRejected(AgentRole role)
    {
        using var fixture = PreparePlanning(role, retry: false, Toolchain.Unknown);
        const string action = "  Artifact: current-task.md\n" +
            "  Use for: read deterministic-verification.md to choose checks.";
        Assert.Equal(["current-task.md"], RequiredArtifacts(action));
        var required = RequiredPlanningArtifacts(action);
        Assert.Equal(["current-task.md", "deterministic-verification.md"], required);
        fixture.AssertPlanningArtifactAvailable("current-task.md");
        var error = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            Assert.All(required, fixture.AssertPlanningArtifactAvailable));
        Assert.Contains("Required broker artifact 'deterministic-verification.md' is not delivered", error.Message, StringComparison.Ordinal);
        Assert.Contains(role.ToString(), error.Message, StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> CorruptionCases()
    {
        foreach (var role in new[] { AgentRole.Planner, AgentRole.Researcher })
        foreach (var corruption in new[] { "goal", "task", "role", "directory", "visibility", "registration", "delivered-content", "missing-delivery" })
            yield return [role, corruption];
    }

    [Theory]
    [MemberData(nameof(CorruptionCases))]
    public void RegisteredDeliveryRejectsCorruption(AgentRole role, string corruption)
    {
        using var fixture = PreparePlanning(role, retry: false, Toolchain.Unknown);
        const string name = "workflow-brokers.md";
        Assert.NotEmpty(fixture.ReadDelivered(name));
        var registryPath = Path.Combine(fixture.ContextDirectory, "artifact-registry.json");
        var originalRegistry = File.ReadAllText(registryPath);
        var registry = JsonNode.Parse(originalRegistry)!.AsObject();
        var artifacts = registry["artifacts"]!.AsArray();
        var entry = Assert.Single(artifacts, artifact => artifact!["path"]!.GetValue<string>() == name)!;
        var section = Assert.Single(fixture.Receipt.Sections, item => item.LogicalIdentity == $"context/{name}");
        var deliveredPath = Path.Combine(fixture.WorkingDirectory, section.MandatoryRelativePath!);
        var originalBytes = File.ReadAllBytes(deliveredPath);
        try
        {
            switch (corruption)
            {
                case "goal": registry["goalId"] = "foreign-goal"; break;
                case "task": registry["taskId"] = "foreign-task"; break;
                case "role": registry["role"] = "Reviewer"; break;
                case "directory": registry["workingDirectory"] = Path.Combine(fixture.Root, "foreign"); break;
                case "visibility": entry["roleVisibility"] = new JsonArray("Reviewer"); break;
                case "registration": artifacts.Remove(entry); break;
                case "delivered-content":
                    var corrupted = originalBytes.ToArray();
                    corrupted[0] ^= 1;
                    File.WriteAllBytes(deliveredPath, corrupted);
                    Assert.Equal(originalBytes.Length, File.ReadAllBytes(deliveredPath).Length);
                    break;
                case "missing-delivery": File.Delete(deliveredPath); break;
                default: throw new ArgumentOutOfRangeException(nameof(corruption));
            }
            File.WriteAllText(registryPath, registry.ToJsonString());
            Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => fixture.ReadDelivered(name));
        }
        finally
        {
            File.WriteAllText(registryPath, originalRegistry);
            File.WriteAllBytes(deliveredPath, originalBytes);
        }
        Assert.NotEmpty(fixture.ReadDelivered(name));
    }

    private static DeliveryFixture PreparePlanning(AgentRole role, bool retry, Toolchain toolchain)
    {
        Assert.Contains(role, new[] { AgentRole.Planner, AgentRole.Researcher });
        var fixture = Prepare(role, retry, includeDotnetProject: toolchain == Toolchain.Dotnet,
            includeGoModule: toolchain == Toolchain.Go, includePackageJson: toolchain == Toolchain.Node,
            pythonMarker: toolchain == Toolchain.Python ? "pyproject.toml" : null);
        try
        {
            Assert.Equal(toolchain, TargetToolchainDetector.Detect(fixture.WorkingDirectory));
            return fixture;
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }
}
