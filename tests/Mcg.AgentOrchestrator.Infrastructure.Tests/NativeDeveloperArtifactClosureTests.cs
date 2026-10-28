using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static BrokerDeliveryTestFixture;

// Parallel-safe preparation only: each case owns its Git repository, kernel, prompts and cleanup.
// No language tool, package manager or provider is invoked by these fixtures.
public sealed class NativeDeveloperArtifactClosureTests
{
    [Theory]
    [InlineData("pyproject.toml", false)]
    [InlineData("pyproject.toml", true)]
    [InlineData("requirements.txt", false)]
    [InlineData("requirements.txt", true)]
    [InlineData("README.md", false)]
    [InlineData("README.md", true)]
    public void DeveloperRequiredArtifactsResolve(string repositoryForm, bool retry)
    {
        using var fixture = PrepareNative(repositoryForm, AgentRole.Developer, retry);
        var actions = ReadActions(fixture.ReadDelivered("workflow-brokers.md"));
        foreach (var name in new[] { "build-test-selection", "static-policy-checks", "acceptance-evidence", "diff-summary" })
        {
            Assert.Contains(name, actions.Keys);
            Assert.NotEmpty(RequiredArtifacts(actions[name]));
            Assert.Contains("  Use for:", actions[name], StringComparison.Ordinal);
            Assert.Contains("  Failure handling:", actions[name], StringComparison.Ordinal);
        }
        // Keep closure first: unchanged baseline assertions must fail on unauthorized checklist
        // delivery, rather than a renderer-specific assertion or an unrelated setup assumption.
        Assert.All(actions.Values.SelectMany(RequiredArtifacts), fixture.AssertArtifactAvailable);

        // The unfiltered checklist exists but is never authority for Developer delivery.
        Assert.True(File.Exists(Path.Combine(fixture.ContextDirectory, "deterministic-verification.md")));
        Assert.DoesNotContain(fixture.Receipt.Sections,
            section => section.LogicalIdentity == "context/deterministic-verification.md");
        Assert.DoesNotContain("context/deterministic-verification.md", fixture.Prompt, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.ContextDirectory, "authoritative"),
            "deterministic-verification.md", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("pyproject.toml", false)]
    [InlineData("pyproject.toml", true)]
    [InlineData("requirements.txt", false)]
    [InlineData("requirements.txt", true)]
    [InlineData("README.md", false)]
    [InlineData("README.md", true)]
    public void DeveloperGuidancePreservesNativeContract(string repositoryForm, bool retry)
    {
        using var fixture = PrepareNative(repositoryForm, AgentRole.Developer, retry);
        var broker = fixture.ReadDelivered("workflow-brokers.md");
        var actions = ReadActions(broker);
        Assert.DoesNotContain("build-only", actions.Keys);
        Assert.DoesNotContain("deterministic-verification.md", broker, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-WorkerBuildCheck", broker, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-IsolatedDotnet", broker, StringComparison.Ordinal);
        var build = actions["build-test-selection"];
        var command = repositoryForm == "README.md" ? "run the project's test suite" : "python -m pytest (or project test command)";
        Assert.Equal($"  Suggested command shape: `{command}`", Assert.Single(build.Split('\n')
            .Where(line => line.StartsWith("  Suggested command shape:", StringComparison.Ordinal))));
        var localChecks = Assert.Single(build.Split('\n')
            .Where(line => line.StartsWith("  Local checks:", StringComparison.Ordinal)));
        if (repositoryForm == "README.md")
        {
            Assert.Contains("Run the project's standard build and test commands.", localChecks, StringComparison.Ordinal);
            Assert.Contains("README or CI configuration", localChecks, StringComparison.Ordinal);
            Assert.Contains("report the missing command and evidence rather than guessing a toolchain", localChecks, StringComparison.Ordinal);
            foreach (var foreignCommand in new[] { "python -m pytest", "npm test", "go test", "dotnet" })
                Assert.DoesNotContain(foreignCommand, broker, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("Use `python -m pytest` (or the project test command) to run tests.", localChecks, StringComparison.Ordinal);
            Assert.Contains("Check pyproject.toml or requirements.txt for test configuration.", localChecks, StringComparison.Ordinal);
        }
        Assert.Contains("Current recommendation:", build, StringComparison.Ordinal);
        Assert.Contains("generated test-impact plan", build, StringComparison.Ordinal);
        Assert.Contains("leave unrun checks explicitly pending", build, StringComparison.Ordinal);
        Assert.Contains("include the command and exit evidence", build, StringComparison.Ordinal);
        var evidence = actions["acceptance-evidence"];
        Assert.Contains("Record commands, exit results, failures and missing evidence", evidence, StringComparison.Ordinal);
        Assert.Contains("never report unrun checks as passed", evidence, StringComparison.Ordinal);
        Assert.Contains("does not authorize a formal acceptance verdict", evidence, StringComparison.Ordinal);
        Assert.Contains("The acceptance gate re-runs the project's acceptance manifest checks and decides the verdict", evidence, StringComparison.Ordinal);
        Assert.Contains("required review remains with its existing owner", evidence, StringComparison.Ordinal);
        Assert.Contains("missing acceptance evidence should become an explicit blocker", evidence, StringComparison.Ordinal);
        Assert.Contains("unexpected dirty files are blockers", actions["diff-summary"], StringComparison.Ordinal);
        fixture.AssertArtifactAvailable("current-task.md");
    }

    [Theory]
    [InlineData("pyproject.toml", false)]
    [InlineData("pyproject.toml", true)]
    [InlineData("requirements.txt", false)]
    [InlineData("requirements.txt", true)]
    [InlineData("README.md", false)]
    [InlineData("README.md", true)]
    public void NativePolicyReportsReviewWithoutAuthority(string repositoryForm, bool retry)
    {
        using var fixture = PrepareNative(repositoryForm, AgentRole.Developer, retry, requireRiskReview: true);
        var actions = ReadActions(fixture.ReadDelivered("workflow-brokers.md"));
        var policy = actions["static-policy-checks"];
        Assert.Contains("No deterministic build/test command required by current file scope.", policy, StringComparison.Ordinal);
        Assert.Contains("human risk review; kind=manual-risk-review; required=True; reason=Task text contains policy, security, migration, rollback, or permission risk signals.", policy, StringComparison.Ordinal);
        Assert.Contains("generated-path risk", policy, StringComparison.Ordinal);
        Assert.Contains("worker-result completeness", policy, StringComparison.Ordinal);
        Assert.Contains("required policy failures block acceptance", policy, StringComparison.Ordinal);
        Assert.Contains("required human review remain with their existing owners", policy, StringComparison.Ordinal);
        Assert.Contains("without expanding local execution permissions", policy, StringComparison.Ordinal);
        Assert.All(actions.Values.SelectMany(RequiredArtifacts), fixture.AssertArtifactAvailable);
    }

    public static IEnumerable<object[]> ChecklistCases()
    {
        foreach (var form in new[] { "pyproject.toml", "requirements.txt", "README.md" })
        foreach (var role in new[] { AgentRole.Tester, AgentRole.Reviewer })
        foreach (var retry in new[] { false, true })
            yield return [form, role, retry];
    }

    [Theory]
    [MemberData(nameof(ChecklistCases))]
    public void ChecklistOwnersKeepRequiredDelivery(string repositoryForm, AgentRole role, bool retry)
    {
        using var fixture = PrepareNative(repositoryForm, role, retry);
        var actions = ReadActions(fixture.ReadDelivered("workflow-brokers.md"));
        var checklist = fixture.ReadDelivered("deterministic-verification.md");
        foreach (var name in new[] { "build-test-selection", "static-policy-checks", "acceptance-evidence" })
        {
            Assert.Contains(name, actions.Keys);
            Assert.Contains("deterministic-verification.md", RequiredArtifacts(actions[name]));
            Assert.DoesNotContain("optional", actions[name], StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("delegated", actions[name], StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(["deterministic-verification.md", "current-task.md"], RequiredArtifacts(actions["acceptance-evidence"]));
        if (role == AgentRole.Reviewer)
        {
            Assert.DoesNotContain("## Python Verification", checklist, StringComparison.Ordinal);
            Assert.Contains("## Test Impact Plan", checklist, StringComparison.Ordinal);
            Assert.Contains("## Required Verification Policy", checklist, StringComparison.Ordinal);
        }
        else if (repositoryForm == "README.md")
            Assert.Contains("Check the repository README or CI configuration for the correct commands.", checklist, StringComparison.Ordinal);
        else
        {
            Assert.Contains("Use `python -m pytest` (or the project test command)", checklist, StringComparison.Ordinal);
            Assert.Contains("Check pyproject.toml or requirements.txt for test configuration.", checklist, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("build-only", actions.Keys);
        Assert.All(actions.Values.SelectMany(RequiredArtifacts), fixture.AssertArtifactAvailable);
    }

    public static IEnumerable<object[]> CorruptionCases()
    {
        foreach (var form in new[] { "pyproject.toml", "requirements.txt", "README.md" })
        foreach (var corruption in new[] { "goal", "task", "role", "directory", "visibility", "registration", "content" })
            yield return [form, corruption];
    }

    [Theory]
    [MemberData(nameof(CorruptionCases))]
    public void RequiredArtifactAuthorityRejectsCorruption(string repositoryForm, string corruption)
    {
        using var fixture = PrepareNative(repositoryForm, AgentRole.Developer, retry: false);
        var actions = ReadActions(fixture.ReadDelivered("workflow-brokers.md"));
        var references = actions.Values.SelectMany(RequiredArtifacts).ToArray();
        Assert.Contains("diff-summary.md", references);
        Assert.All(references, fixture.AssertArtifactAvailable);
        var registryPath = Path.Combine(fixture.ContextDirectory, "artifact-registry.json");
        var originalRegistry = File.ReadAllText(registryPath);
        var registry = JsonNode.Parse(originalRegistry)!.AsObject();
        var artifacts = registry["artifacts"]!.AsArray();
        var diff = Assert.Single(artifacts, entry => entry!["path"]!.GetValue<string>() == "diff-summary.md")!;
        var diffPath = Path.Combine(fixture.ContextDirectory, "diff-summary.md");
        var originalDiff = File.ReadAllText(diffPath);
        switch (corruption)
        {
            case "goal": registry["goalId"] = "another-goal"; break;
            case "task": registry["taskId"] = "another-task"; break;
            case "role": registry["role"] = "Reviewer"; break;
            case "directory": registry["workingDirectory"] = Path.Combine(fixture.Root, "foreign"); break;
            case "visibility": diff["roleVisibility"] = new JsonArray("Reviewer"); break;
            case "registration": artifacts.Remove(diff); break;
            case "content":
                // Equal-length corruption exercises the hash check, even with the receipt identity intact.
                var changed = originalDiff.Replace("fixture.txt", "foreign.txt", StringComparison.Ordinal);
                Assert.NotEqual(originalDiff, changed);
                File.WriteAllText(diffPath, changed);
                Assert.Equal(new FileInfo(diffPath).Length, diff["byteCount"]!.GetValue<long>());
                break;
            default: throw new ArgumentOutOfRangeException(nameof(corruption));
        }
        File.WriteAllText(registryPath, registry.ToJsonString());
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => Assert.All(references, fixture.AssertArtifactAvailable));
        File.WriteAllText(registryPath, originalRegistry);
        File.WriteAllText(diffPath, originalDiff);
        Assert.All(references, fixture.AssertArtifactAvailable);
    }

    private static DeliveryFixture PrepareNative(string repositoryForm, AgentRole role, bool retry,
        bool requireRiskReview = false)
    {
        Assert.Contains(repositoryForm, new[] { "pyproject.toml", "requirements.txt", "README.md" });
        var fixture = Prepare(role, retry, includeDotnetProject: false, requireRiskReview: requireRiskReview,
            pythonMarker: repositoryForm == "README.md" ? null : repositoryForm);
        try
        {
            Assert.Equal(repositoryForm == "README.md" ? Toolchain.Unknown : Toolchain.Python,
                TargetToolchainDetector.Detect(fixture.WorkingDirectory));
            Assert.True(File.Exists(Path.Combine(fixture.WorkingDirectory, repositoryForm)));
            return fixture;
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }
}
