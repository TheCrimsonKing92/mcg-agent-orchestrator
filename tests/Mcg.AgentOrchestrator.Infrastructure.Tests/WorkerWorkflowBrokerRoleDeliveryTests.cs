using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each preparation owns its repository, kernel, prompts, and cleanup root.
public sealed class WorkerWorkflowBrokerRoleDeliveryTests : WorkerDispatchTestSupport
{
    private const string Feedback = "broker retry: preserve complete-scope build-only delivery";

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
        fixture.AssertArtifactAvailable("current-task.md");
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

    private static Dictionary<string, string> ReadActions(string broker)
    {
        var start = broker.IndexOf("## Available Broker Actions\n", StringComparison.Ordinal);
        Assert.True(start >= 0, "Delivered broker must have available actions.");
        start += "## Available Broker Actions\n".Length;
        var end = broker.IndexOf("\n## Broker Output Contract", start, StringComparison.Ordinal);
        Assert.True(end > start, "Delivered broker must have a nonempty action section.");
        return Regex.Matches(broker[start..end], @"(?m)^- (?<name>[^\n]+)\n(?<body>(?:(?!^- ).|\n)*)")
            .ToDictionary(match => match.Groups["name"].Value, match => match.Groups["body"].Value,
                StringComparer.Ordinal);
    }

    private static string[] RequiredArtifacts(string action) => Regex.Matches(action, @"(?m)^  Artifact: (?<paths>[^\n]+)$")
        .SelectMany(match => match.Groups["paths"].Value.Split(" plus ", StringSplitOptions.TrimEntries))
        .ToArray();

    private static DeliveryFixture Prepare(AgentRole role, bool retry,
        bool includeDotnetProject = true, bool includeGoModule = false)
    {
        var root = CreateTempDirectory();
        try
        {
            var workingDirectory = Path.Combine(root, "repo");
            Directory.CreateDirectory(workingDirectory);
            Git("init", "--initial-branch=main");
            File.WriteAllText(Path.Combine(workingDirectory, "fixture.txt"), "broker fixture");
            if (includeDotnetProject)
            {
                // Use the conventional project layout recognized by the existing detector.
                var projectDirectory = Path.Combine(workingDirectory, "src", "Fixture");
                Directory.CreateDirectory(projectDirectory);
                File.WriteAllText(Path.Combine(projectDirectory, "Fixture.csproj"),
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            }
            if (includeGoModule)
                File.WriteAllText(Path.Combine(workingDirectory, "go.mod"), "module example.invalid/broker\n\ngo 1.21\n");
            Assert.Equal(includeGoModule ? Toolchain.Go : includeDotnetProject ? Toolchain.Dotnet : Toolchain.Unknown,
                TargetToolchainDetector.Detect(workingDirectory));
            Git("add", ".");
            Git("-c", "user.name=Broker Tests", "-c", "user.email=broker@example.invalid", "commit", "--quiet", "-m", "fixture");
            var head = Git("rev-parse", "HEAD").Trim();
            // A real dirty file makes the diff artifact distinguishable from a boilerplate file.
            File.AppendAllText(Path.Combine(workingDirectory, "fixture.txt"), "\nchanged broker fixture");
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Keep broker artifacts satisfiable.", role);
            var goal = kernel.CreateGoal("Repair broker instructions.", [task]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            if (retry)
            {
                var location = new ReviewFindingLocation("fixture.txt", "broker");
                kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                    "review", workingDirectory, 1, "broker requires clarification", string.Empty,
                    DateTimeOffset.UtcNow, ReviewedCommit: head, ReviewFindingTouchedAnchors: [location],
                    MergedReviewFindings: [new ReviewFinding("broker-contract", ReviewFindingState.Open, location, "repair instructions")],
                    FullStandardOutput: "broker requires clarification", FullStandardError: string.Empty));
                kernel.RetryTaskWithAuthoritativeFeedback(goal.Id, task.Id, Feedback,
                    RetryCause.ContractClarification, retryRoundKind: RetryRoundKind.Standard);
            }
            var prepared = WorkerProfileDispatcher.PrepareTask(kernel, goal, task,
                new WorkerProfile("codex-cli", "codex exec --sandbox {sandboxMode} --cd {workingDirectory}"),
                Path.Combine(root, "prompts"), workingDirectory, DateTimeOffset.UtcNow,
                providerName: "OpenAI", modelName: AgentCatalog.OpenAiSolSubscriptionModelAlias,
                reviewerScopeChangedFiles: role == AgentRole.Reviewer ? ["fixture.txt"] : null,
                reviewerScopeMergeBase: role == AgentRole.Reviewer ? head : null,
                reviewerScopeTotalChangedFileCount: role == AgentRole.Reviewer ? 1 : null,
                reviewerMergeTreeClean: role == AgentRole.Reviewer ? true : null,
                reviewerMergeTreeConflictPaths: role == AgentRole.Reviewer ? [] : null,
                reviewerMergeTreeTotalConflictPathCount: role == AgentRole.Reviewer ? 0 : null);
            var receipt = prepared.Task.LastDispatch!.ContextPackageReceipt!;
            Assert.NotNull(receipt);
            var prompt = File.ReadAllText(prepared.PromptPath);
            if (retry)
            {
                var feedbackReceipt = receipt.RetryFeedbackPromptReceipt;
                Assert.NotNull(feedbackReceipt);
                Assert.Equal(task.Id.Value, feedbackReceipt.AcceptedRetryTaskId);
                Assert.Equal(WorkerContextArtifact.Hash(Encoding.UTF8.GetBytes(Feedback)), feedbackReceipt.AcceptedFeedbackSha256);
                Assert.Equal(WorkerContextArtifact.Hash(File.ReadAllBytes(prepared.PromptPath)), feedbackReceipt.GeneratedPromptSha256);
                Assert.Contains("identity=task/criterion-retry-feedback.json;", prompt, StringComparison.Ordinal);
                Assert.Contains(Feedback, prompt, StringComparison.Ordinal);
            }
            else
            {
                Assert.Null(receipt.RetryFeedbackPromptReceipt);
            }
            return new DeliveryFixture(root, workingDirectory,
                Path.Combine(workingDirectory, ".orchestrator-context", goal.Id.Value), role, prompt, receipt);

            string Git(params string[] args)
            {
                var result = GitCli.Run(workingDirectory, args);
                Assert.True(result.Succeeded && !result.DrainTimedOut,
                    $"git fixture failed: exit={result.ExitCode}; stderr={result.Error}");
                return result.Output;
            }
        }
        catch
        {
            SharedTestSupport.RemoveTempDirectory(root);
            throw;
        }
    }

    private sealed record DeliveryFixture(string Root, string WorkingDirectory, string ContextDirectory,
        AgentRole Role, string Prompt, WorkerContextPackageReceipt Receipt) : IDisposable
    {
        public string ReadDelivered(string name)
        {
            var identity = $"context/{name}";
            Assert.True(Receipt.Sections.Any(candidate => candidate.LogicalIdentity == identity),
                $"Required broker artifact '{name}' is not delivered to {Role}; unfiltered file existence is insufficient.");
            var section = Assert.Single(Receipt.Sections, candidate => candidate.LogicalIdentity == identity);
            Assert.Contains(Role, section.RoleVisibility);
            Assert.Equal(ContextDeliveryMode.MandatoryFile, section.DeliveryMode);
            Assert.NotNull(section.MandatoryRelativePath);
            var path = Path.Combine(WorkingDirectory, section.MandatoryRelativePath);
            Assert.True(File.Exists(path), $"Missing delivered artifact: {path}");
            Assert.Equal(section.ByteCount, File.ReadAllBytes(path).Length);
            Assert.Equal(section.ContentHash, WorkerContextArtifact.Hash(File.ReadAllBytes(path)));
            AssertPromptReceipt(section);
            Assert.NotEmpty(ReadProjection(name));
            Assert.Equal(section.ContentHash,
                WorkerContextArtifact.Hash(File.ReadAllBytes(Path.Combine(ContextDirectory, name))));
            return File.ReadAllText(path).ReplaceLineEndings("\n");
        }

        public string ReadProjection(string name)
        {
            var path = Path.Combine(ContextDirectory, name);
            Assert.True(File.Exists(path), $"Missing domain projection: {name}");
            using var registry = JsonDocument.Parse(File.ReadAllText(Path.Combine(ContextDirectory, "artifact-registry.json")));
            var entry = Assert.Single(registry.RootElement.GetProperty("artifacts").EnumerateArray(),
                item => item.GetProperty("path").GetString() == name);
            Assert.True(entry.GetProperty("exists").GetBoolean());
            Assert.True(entry.GetProperty("hashVerified").GetBoolean());
            Assert.Contains(Role.ToString(), entry.GetProperty("roleVisibility").EnumerateArray().Select(item => item.GetString()));
            Assert.Equal(entry.GetProperty("byteCount").GetInt64(), File.ReadAllBytes(path).LongLength);
            Assert.Equal(entry.GetProperty("sha256").GetString(), WorkerContextArtifact.Hash(File.ReadAllBytes(path)));
            return File.ReadAllText(path);
        }

        private void AssertPromptReceipt(WorkerContextSectionReceipt section)
        {
            var declaration = Assert.Single(Prompt.Split('\n'),
                line => line.Contains($"identity={section.LogicalIdentity};", StringComparison.Ordinal));
            Assert.Contains($"sha256={section.ContentHash};", declaration, StringComparison.Ordinal);
        }

        public void AssertArtifactAvailable(string name)
        {
            if (name == "current-task.md")
            {
                Assert.Contains("Keep broker artifacts satisfiable.", ReadProjection(name), StringComparison.Ordinal);
                var description = Assert.Single(Receipt.Sections, section => section.LogicalIdentity == "task/description.md");
                Assert.Equal(WorkerContextArtifact.Hash(Encoding.UTF8.GetBytes("Keep broker artifacts satisfiable.")), description.ContentHash);
                Assert.Contains(Role, description.RoleVisibility);
                AssertPromptReceipt(description);
                Assert.Contains("Keep broker artifacts satisfiable.", Prompt, StringComparison.Ordinal);
                var brief = Assert.Single(Receipt.Sections, section => section.LogicalIdentity == "brief/current.md");
                Assert.Contains(Role, brief.RoleVisibility);
                AssertPromptReceipt(brief);
                if (Role == AgentRole.Developer)
                {
                    if (TargetToolchainDetector.Detect(WorkingDirectory) == Toolchain.Go)
                    {
                        var task = ReadProjection(name);
                        Assert.Contains("Run `go build ./...`, `go vet ./...` and `go test ./...`", task, StringComparison.Ordinal);
                        Assert.Contains("Report each result in WORKER_RESULT", task, StringComparison.Ordinal);
                        Assert.Contains("The acceptance gate re-runs the Go checks and decides the verdict.", task, StringComparison.Ordinal);
                    }
                    else
                        Assert.Contains("Build changed projects with the worker build check.", Prompt, StringComparison.Ordinal);
                }
            }
            else if (name == "diff-summary.md")
            {
                Assert.Contains("fixture.txt", ReadProjection(name), StringComparison.Ordinal);
                Assert.Contains("diff-summary.md", ReadDelivered("workflow-brokers.md"), StringComparison.Ordinal);
            }
            else
            {
                Assert.NotEmpty(ReadDelivered(name));
            }
        }

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }
}
