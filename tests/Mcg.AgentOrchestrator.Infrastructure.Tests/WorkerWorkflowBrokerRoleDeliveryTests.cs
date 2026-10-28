using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static BrokerDeliveryTestFixture;

// Parallel-safe: each preparation owns its repository, kernel, prompts, and cleanup root.
public sealed class WorkerWorkflowBrokerRoleDeliveryTests : WorkerDispatchTestSupport
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeveloperActionsResolveDeliveredArtifacts(bool retry)
    {
        using var fixture = Prepare(AgentRole.Developer, retry);
        var actions = ReadActions(fixture.ReadDelivered("workflow-brokers.md"));

        Assert.NotEmpty(actions);
        Assert.Contains("build-only", actions.Keys);
        Assert.Contains("diff-summary", actions.Keys);
        Assert.Equal(["current-task.md"], RequiredArtifacts(actions["build-only"]));
        Assert.Equal(["diff-summary.md"], RequiredArtifacts(actions["diff-summary"]));
        Assert.All(actions.Values.SelectMany(RequiredArtifacts), artifact =>
        {
            Assert.NotEqual("deterministic-verification.md", artifact);
            fixture.AssertArtifactAvailable(artifact);
        });

        // The unfiltered producer still creates the checklist; authoritative delivery excludes it.
        Assert.True(File.Exists(Path.Combine(fixture.ContextDirectory, "deterministic-verification.md")));
        Assert.DoesNotContain(fixture.Receipt.Sections,
            section => section.LogicalIdentity == "context/deterministic-verification.md");
        Assert.DoesNotContain("context/deterministic-verification.md", fixture.Prompt, StringComparison.Ordinal);
        var authoritativeContext = Path.Combine(fixture.ContextDirectory, "authoritative");
        Assert.Empty(Directory.EnumerateFiles(authoritativeContext, "deterministic-verification.md",
            SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(AgentRole.Tester, false, false)]
    [InlineData(AgentRole.Tester, true, false)]
    [InlineData(AgentRole.Reviewer, false, false)]
    [InlineData(AgentRole.Reviewer, true, false)]
    [InlineData(AgentRole.Tester, false, true)]
    [InlineData(AgentRole.Tester, true, true)]
    [InlineData(AgentRole.Reviewer, false, true)]
    [InlineData(AgentRole.Reviewer, true, true)]
    public void ChecklistRolesRetainRequiredArtifacts(AgentRole role, bool retry, bool includeGoModule)
    {
        using var fixture = Prepare(role, retry, includeDotnetProject: !includeGoModule, includeGoModule: includeGoModule);
        var actions = ReadActions(fixture.ReadDelivered("workflow-brokers.md"));
        Assert.NotEmpty(fixture.ReadDelivered("deterministic-verification.md"));
        Assert.DoesNotContain("build-only", actions.Keys);
        foreach (var name in new[] { "build-test-selection", "static-policy-checks", "acceptance-evidence" })
        {
            // A top-level available action with an unqualified Artifact declaration is current-role required work.
            Assert.Contains(name, actions.Keys);
            var action = actions[name];
            Assert.Contains("deterministic-verification.md", RequiredArtifacts(action));
            Assert.DoesNotContain("optional", action, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("delegated", action, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(["deterministic-verification.md", "current-task.md"],
            RequiredArtifacts(actions["acceptance-evidence"]));
        Assert.All(actions.Values.SelectMany(RequiredArtifacts), fixture.AssertArtifactAvailable);
    }

    [Theory]
    [InlineData(AgentRole.Tester, false)]
    [InlineData(AgentRole.Tester, true)]
    [InlineData(AgentRole.Reviewer, false)]
    [InlineData(AgentRole.Reviewer, true)]
    public void NodeChecklistRolesRetainRequiredArtifacts(AgentRole role, bool retry)
    {
        using var fixture = Prepare(role, retry, includeDotnetProject: false, includePackageJson: true);
        var actions = ReadActions(fixture.ReadDelivered("workflow-brokers.md"));
        var checklist = fixture.ReadDelivered("deterministic-verification.md");
        if (role == AgentRole.Tester)
        {
            Assert.Contains("Use `npm test` (or the package.json test script)", checklist, StringComparison.Ordinal);
            Assert.Contains("Use `npm run build`", checklist, StringComparison.Ordinal);
        }
        else
        {
            // Reviewer receives policy/evidence requirements, not Developer/Tester execution instructions.
            Assert.DoesNotContain("## Node.js Verification", checklist, StringComparison.Ordinal);
            Assert.Contains("## Test Impact Plan", checklist, StringComparison.Ordinal);
            Assert.Contains("## Required Verification Policy", checklist, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("build-only", actions.Keys);
        foreach (var name in new[] { "build-test-selection", "static-policy-checks", "acceptance-evidence" })
        {
            Assert.Contains(name, actions.Keys);
            Assert.Contains("deterministic-verification.md", RequiredArtifacts(actions[name]));
            Assert.DoesNotContain("optional", actions[name], StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("delegated", actions[name], StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(["deterministic-verification.md", "current-task.md"],
            RequiredArtifacts(actions["acceptance-evidence"]));
        Assert.All(actions.Values.SelectMany(RequiredArtifacts), fixture.AssertArtifactAvailable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeveloperActionsKeepBuildOnlyContract(bool retry)
    {
        using var fixture = Prepare(AgentRole.Developer, retry);
        var broker = fixture.ReadDelivered("workflow-brokers.md");
        Assert.Contains("If a broker artifact is missing, stale, or reports a failure, report that as a blocker in WORKER_RESULT",
            broker, StringComparison.Ordinal);
        var actions = ReadActions(broker);
        Assert.Contains("build-only", actions.Keys);
        var build = actions["build-only"];
        Assert.DoesNotContain("build-test-selection", actions.Keys);
        Assert.DoesNotContain("static-policy-checks", actions.Keys);
        Assert.DoesNotContain("acceptance-evidence", actions.Keys);
        Assert.Contains("complete-scope build-only", build, StringComparison.Ordinal);
        Assert.Contains("every project whose sources you changed", build, StringComparison.Ordinal);
        Assert.Contains("current-task.md", build, StringComparison.Ordinal);
        Assert.Contains("Report the build result in WORKER_RESULT", build, StringComparison.Ordinal);
        Assert.Contains("Automated test execution and formal verification evidence", build, StringComparison.Ordinal);
        Assert.Contains("conductor verification/Reviewer pipeline", build, StringComparison.Ordinal);

        var invocation = OrchestratorBuildEvidenceCheck.FormatScriptInvocation(
            OrchestratorBuildEvidenceCheck.ResolveScriptPath());
        var command = Assert.Single(build.Split('\n').Where(line => line.StartsWith("  Suggested command shape:", StringComparison.Ordinal)));
        Assert.Equal($"  Suggested command shape: `{invocation} <project.csproj> [project.csproj...]`", command);
        var runnableLines = string.Join("\n", actions.Values.SelectMany(action => action.Split('\n'))
            .Where(line => Regex.IsMatch(line, @"^  (?:Suggested command shape|Current commands|Command):")));
        Assert.DoesNotMatch(@"(?i)dotnet\s+test|Invoke-TestSummary|Invoke-IsolatedDotnet|testhost", runnableLines);

        var task = fixture.ReadProjection("current-task.md");
        Assert.Contains(invocation + " <project.csproj> [project.csproj...]", task, StringComparison.Ordinal);
        Assert.Contains("Compiling every changed project is required", task, StringComparison.Ordinal);
        Assert.Contains("tests belong to the acceptance gate", task, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void GoDeveloperActionsRetainNativeCommands(bool includeDotnetProject, bool retry)
    {
        using var fixture = Prepare(AgentRole.Developer, retry, includeDotnetProject, includeGoModule: true);
        Assert.True(File.Exists(Path.Combine(fixture.WorkingDirectory, "go.mod")));
        Assert.Equal(includeDotnetProject,
            File.Exists(Path.Combine(fixture.WorkingDirectory, "src", "Fixture", "Fixture.csproj")));
        var broker = fixture.ReadDelivered("workflow-brokers.md");
        var actions = ReadActions(broker);

        Assert.Contains("build-test-selection", actions.Keys);
        var command = Assert.Single(actions["build-test-selection"].Split('\n')
            .Where(line => line.StartsWith("  Suggested command shape:", StringComparison.Ordinal)));
        Assert.Equal("  Suggested command shape: `go test ./...`", command);
        Assert.DoesNotContain("build-only", actions.Keys);
        Assert.Contains("static-policy-checks", actions.Keys);
        Assert.Contains("acceptance-evidence", actions.Keys);
        Assert.Contains("diff-summary", actions.Keys);
        Assert.DoesNotContain("Invoke-WorkerBuildCheck", broker, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-IsolatedDotnet", broker, StringComparison.Ordinal);
        Assert.DoesNotContain("deterministic-verification.md", broker, StringComparison.Ordinal);
        Assert.Contains("Run `go build ./...`, `go vet ./...` and `go test ./...`",
            actions["build-test-selection"], StringComparison.Ordinal);
        Assert.Contains("current-task.md", RequiredArtifacts(actions["build-test-selection"]));
        Assert.Contains("Current recommendation:", actions["build-test-selection"], StringComparison.Ordinal);
        Assert.Contains("generated test-impact plan", actions["build-test-selection"], StringComparison.Ordinal);
        Assert.Contains("include the command and exit evidence", actions["build-test-selection"], StringComparison.Ordinal);
        Assert.Contains("generated-path risk", actions["static-policy-checks"], StringComparison.Ordinal);
        Assert.Contains("Current policy:", actions["static-policy-checks"], StringComparison.Ordinal);
        Assert.Contains("required policy failures block acceptance", actions["static-policy-checks"], StringComparison.Ordinal);
        Assert.Contains("never report unrun checks as passed", actions["acceptance-evidence"], StringComparison.Ordinal);
        Assert.Contains("does not authorize a formal acceptance verdict", actions["acceptance-evidence"], StringComparison.Ordinal);
        Assert.Contains("The acceptance gate re-runs the Go checks and decides the verdict",
            actions["acceptance-evidence"], StringComparison.Ordinal);
        Assert.Contains("missing acceptance evidence should become an explicit blocker", actions["acceptance-evidence"], StringComparison.Ordinal);
        Assert.Contains("unexpected dirty files are blockers", actions["diff-summary"], StringComparison.Ordinal);
        Assert.Contains("If a broker artifact is missing, stale, or reports a failure, report that as a blocker in WORKER_RESULT",
            broker, StringComparison.Ordinal);
        fixture.AssertArtifactAvailable("current-task.md");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GoDeveloperPolicyReportsRequiredReviewWithoutGrantingAuthority(bool retry)
    {
        using var fixture = Prepare(AgentRole.Developer, retry, includeDotnetProject: false,
            includeGoModule: true, requireRiskReview: true);
        var actions = ReadActions(fixture.ReadDelivered("workflow-brokers.md"));
        var policy = actions["static-policy-checks"];
        // The summary alone omits required human review when no deterministic command is selected.
        Assert.Contains("No deterministic build/test command required by current file scope.", policy, StringComparison.Ordinal);
        Assert.Contains("human risk review; kind=manual-risk-review; required=True", policy, StringComparison.Ordinal);
        Assert.Contains("Task text contains policy, security, migration, rollback, or permission risk signals.", policy, StringComparison.Ordinal);
        Assert.Contains("required human review remain with their existing owners", policy, StringComparison.Ordinal);
        Assert.All(actions.Values.SelectMany(RequiredArtifacts), fixture.AssertArtifactAvailable);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void GoDeveloperActionsResolveDeliveredArtifacts(bool includeDotnetProject, bool retry)
    {
        using var fixture = Prepare(AgentRole.Developer, retry, includeDotnetProject, includeGoModule: true);
        var actions = ReadActions(fixture.ReadDelivered("workflow-brokers.md"));
        foreach (var name in new[] { "build-test-selection", "static-policy-checks", "acceptance-evidence", "diff-summary" })
        {
            Assert.Contains(name, actions.Keys);
            Assert.NotEmpty(RequiredArtifacts(actions[name]));
            Assert.Contains("  Use for:", actions[name], StringComparison.Ordinal);
            Assert.Contains("  Failure handling:", actions[name], StringComparison.Ordinal);
        }
        Assert.All(actions.Values.SelectMany(RequiredArtifacts), fixture.AssertArtifactAvailable);

        // Unfiltered existence must not mask a missing role-authorized delivery.
        Assert.True(File.Exists(Path.Combine(fixture.ContextDirectory, "deterministic-verification.md")));
        Assert.DoesNotContain(fixture.Receipt.Sections,
            section => section.LogicalIdentity == "context/deterministic-verification.md");
        Assert.DoesNotContain("context/deterministic-verification.md", fixture.Prompt, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.ContextDirectory, "authoritative"),
            "deterministic-verification.md", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NodeDeveloperActionsResolveDeliveredArtifacts(bool includeDotnetProject, bool retry)
    {
        using var fixture = Prepare(AgentRole.Developer, retry, includeDotnetProject, includePackageJson: true);
        Assert.Equal(Toolchain.Node, TargetToolchainDetector.Detect(fixture.WorkingDirectory));
        Assert.True(File.Exists(Path.Combine(fixture.WorkingDirectory, "package.json")));
        Assert.Equal(includeDotnetProject, File.Exists(Path.Combine(fixture.WorkingDirectory, "Fixture.csproj")));
        var actions = ReadActions(fixture.ReadDelivered("workflow-brokers.md"));
        foreach (var name in new[] { "build-test-selection", "static-policy-checks", "acceptance-evidence", "diff-summary" })
        {
            Assert.Contains(name, actions.Keys);
            Assert.NotEmpty(RequiredArtifacts(actions[name]));
            Assert.Contains("  Use for:", actions[name], StringComparison.Ordinal);
            Assert.Contains("  Failure handling:", actions[name], StringComparison.Ordinal);
        }
        // Keep closure first so the same assertion on baseline fails on missing checklist delivery.
        Assert.All(actions.Values.SelectMany(RequiredArtifacts), fixture.AssertArtifactAvailable);
        Assert.True(File.Exists(Path.Combine(fixture.ContextDirectory, "deterministic-verification.md")));
        Assert.DoesNotContain(fixture.Receipt.Sections,
            section => section.LogicalIdentity == "context/deterministic-verification.md");
        Assert.DoesNotContain("context/deterministic-verification.md", fixture.Prompt, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.ContextDirectory, "authoritative"),
            "deterministic-verification.md", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NodeDeveloperActionsRetainNativeCommandsAndGateOwnership(bool includeDotnetProject, bool retry)
    {
        using var fixture = Prepare(AgentRole.Developer, retry, includeDotnetProject, includePackageJson: true);
        var broker = fixture.ReadDelivered("workflow-brokers.md");
        var actions = ReadActions(broker);
        var build = actions["build-test-selection"];
        Assert.DoesNotContain("build-only", actions.Keys);
        Assert.DoesNotContain("Invoke-WorkerBuildCheck", broker, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-IsolatedDotnet", broker, StringComparison.Ordinal);
        Assert.DoesNotContain("deterministic-verification.md", broker, StringComparison.Ordinal);
        Assert.Equal("  Suggested command shape: `npm test (or package.json test script)`",
            Assert.Single(build.Split('\n').Where(line => line.StartsWith("  Suggested command shape:", StringComparison.Ordinal))));
        var localChecks = Assert.Single(build.Split('\n').Where(line => line.StartsWith("  Local checks:", StringComparison.Ordinal)));
        Assert.Contains("Use `npm test` (or the package.json test script) to run the test suite.", localChecks, StringComparison.Ordinal);
        Assert.Contains("Use `npm run build` to verify the project builds.", localChecks, StringComparison.Ordinal);
        Assert.Contains("Check package.json scripts for the project's test and lint commands.", localChecks, StringComparison.Ordinal);
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
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NodeDeveloperPolicyReportsRequiredReviewWithoutGrantingAuthority(bool retry)
    {
        using var fixture = Prepare(AgentRole.Developer, retry, includeDotnetProject: false,
            requireRiskReview: true, includePackageJson: true);
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

    [Theory]
    [InlineData("goal")]
    [InlineData("task")]
    [InlineData("role")]
    [InlineData("visibility")]
    [InlineData("content")]
    [InlineData("registration")]
    public void DiffProjectionRejectsInvalidIdentityVisibilityOrContent(string corruption)
    {
        using var fixture = Prepare(AgentRole.Developer, retry: false, includeDotnetProject: false, includePackageJson: true);
        fixture.AssertArtifactAvailable("diff-summary.md");
        var registryPath = Path.Combine(fixture.ContextDirectory, "artifact-registry.json");
        var registry = JsonNode.Parse(File.ReadAllText(registryPath))!.AsObject();
        var artifacts = registry["artifacts"]!.AsArray();
        var diff = Assert.Single(artifacts, entry => entry!["path"]!.GetValue<string>() == "diff-summary.md")!;
        switch (corruption)
        {
            case "goal": registry["goalId"] = "another-goal"; break;
            case "task": registry["taskId"] = "another-task"; break;
            case "role": registry["role"] = "Reviewer"; break;
            case "visibility": diff["roleVisibility"] = new JsonArray("Reviewer"); break;
            case "content":
                var diffPath = Path.Combine(fixture.ContextDirectory, "diff-summary.md");
                // Equal-length corruption reaches the hash check rather than just the byte count.
                File.WriteAllText(diffPath, File.ReadAllText(diffPath).Replace("fixture.txt", "foreign.txt", StringComparison.Ordinal));
                break;
            case "registration": artifacts.Remove(diff); break;
            default: throw new ArgumentOutOfRangeException(nameof(corruption));
        }
        File.WriteAllText(registryPath, registry.ToJsonString());
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => fixture.AssertArtifactAvailable("diff-summary.md"));
    }

}
