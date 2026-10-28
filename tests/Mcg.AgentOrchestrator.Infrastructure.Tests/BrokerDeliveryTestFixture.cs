using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Every preparation owns its temporary Git repository, kernel, prompts and cleanup; no provider is launched.
internal static class BrokerDeliveryTestFixture
{
    private const string Feedback = "broker retry: preserve complete-scope build-only delivery";
    internal static Dictionary<string, string> ReadActions(string broker)
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

    internal static string[] RequiredArtifacts(string action) => Regex.Matches(action, @"(?m)^  Artifact: (?<paths>[^\n]+)$")
        .SelectMany(match => match.Groups["paths"].Value.Split(" plus ", StringSplitOptions.TrimEntries))
        .ToArray();

    internal static DeliveryFixture Prepare(AgentRole role, bool retry,
        bool includeDotnetProject = true, bool includeGoModule = false, bool requireRiskReview = false,
        bool includePackageJson = false, string? pythonMarker = null)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var workingDirectory = Path.Combine(root, "repo");
            Directory.CreateDirectory(workingDirectory);
            Git("init", "--initial-branch=main");
            File.WriteAllText(Path.Combine(workingDirectory, "fixture.txt"), "broker fixture");
            if (includeDotnetProject)
            {
                // Node precedence is pinned against a root csproj; existing .NET/Go cases keep their layout.
                var projectDirectory = includePackageJson ? workingDirectory : Path.Combine(workingDirectory, "src", "Fixture");
                Directory.CreateDirectory(projectDirectory);
                File.WriteAllText(Path.Combine(projectDirectory, "Fixture.csproj"),
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            }
            if (includeGoModule)
                File.WriteAllText(Path.Combine(workingDirectory, "go.mod"), "module example.invalid/broker\n\ngo 1.21\n");
            if (includePackageJson)
                File.WriteAllText(Path.Combine(workingDirectory, "package.json"),
                    """{"name":"broker-fixture","private":true,"scripts":{"test":"node --test","build":"node build.js","lint":"eslint ."}}""");
            if (pythonMarker is not null)
            {
                Assert.Contains(pythonMarker, new[] { "pyproject.toml", "requirements.txt" });
                File.WriteAllText(Path.Combine(workingDirectory, pythonMarker), pythonMarker == "pyproject.toml"
                    ? "[project]\nname = \"broker-fixture\"\nversion = \"0.0.0\"\n[tool.pytest.ini_options]\ntestpaths = [\"tests\"]\n"
                    : "pytest\n");
            }
            if (!includeDotnetProject && !includeGoModule && !includePackageJson)
                File.WriteAllText(Path.Combine(workingDirectory, "README.md"),
                    "# Broker fixture\nDiscover the project-standard build and test commands from the project CI configuration.\n");
            Assert.Equal(includeGoModule ? Toolchain.Go : includePackageJson ? Toolchain.Node : includeDotnetProject ? Toolchain.Dotnet : pythonMarker is not null ? Toolchain.Python : Toolchain.Unknown,
                TargetToolchainDetector.Detect(workingDirectory));
            Git("add", ".");
            Git("-c", "user.name=Broker Tests", "-c", "user.email=broker@example.invalid", "commit", "--quiet", "-m", "fixture");
            var head = Git("rev-parse", "HEAD").Trim();
            // A real dirty file makes the diff artifact distinguishable from a boilerplate file.
            File.AppendAllText(Path.Combine(workingDirectory, "fixture.txt"), "\nchanged broker fixture");
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Keep broker artifacts satisfiable.", role);
            var goal = kernel.CreateGoal(requireRiskReview
                ? "Repair broker instructions with security review." : "Repair broker instructions.", [task]);
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
                reviewerMergeTreeTotalConflictPathCount: role == AgentRole.Reviewer ? 0 : null,
                targetHome: includePackageJson || (!includeDotnetProject && !includeGoModule) ? WorkerTargetHome.NotHome : null);
            var receipt = prepared.Task.LastDispatch!.ContextPackageReceipt!;
            Assert.NotNull(receipt);
            var prompt = File.ReadAllText(prepared.PromptPath);
            Assert.StartsWith("ctxpkg-v1-sha256:", receipt.SemanticPackageId, StringComparison.Ordinal);
            Assert.Contains($"Semantic package identity (attestation/cache only; not delivery proof): {receipt.SemanticPackageId}", prompt, StringComparison.Ordinal);
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
                Path.Combine(workingDirectory, ".orchestrator-context", goal.Id.Value), role, prompt, receipt,
                goal.Id.Value, task.Id.Value);

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

    internal sealed record DeliveryFixture(string Root, string WorkingDirectory, string ContextDirectory,
        AgentRole Role, string Prompt, WorkerContextPackageReceipt Receipt, string GoalId, string TaskId) : IDisposable
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
            // Domain projections are deliberately absent from Receipt.Sections. Bind their registry
            // to this package's delivered metadata before trusting visibility or content hashes.
            using var metadata = JsonDocument.Parse(ReadInlineDelivered("task/metadata.json"));
            Assert.Equal(GoalId, metadata.RootElement.GetProperty("GoalId").GetString());
            Assert.Equal(TaskId, metadata.RootElement.GetProperty("TaskId").GetString());
            Assert.Equal(metadata.RootElement.GetProperty("GoalId").GetString(), registry.RootElement.GetProperty("goalId").GetString());
            Assert.Equal(metadata.RootElement.GetProperty("TaskId").GetString(), registry.RootElement.GetProperty("taskId").GetString());
            Assert.Equal(Role.ToString(), metadata.RootElement.GetProperty("TaskRole").GetString());
            Assert.Equal(Role.ToString(), registry.RootElement.GetProperty("role").GetString());
            Assert.Equal(WorkingDirectory, registry.RootElement.GetProperty("workingDirectory").GetString());
            using var manifest = JsonDocument.Parse(ReadInlineDelivered("context/manifest.v1.json"));
            Assert.Contains("artifact-registry.json", manifest.RootElement.GetProperty("CompatibilityAliases")
                .EnumerateArray().Select(alias => alias.GetString()));
            var entry = Assert.Single(registry.RootElement.GetProperty("artifacts").EnumerateArray(),
                item => item.GetProperty("path").GetString() == name);
            Assert.True(entry.GetProperty("exists").GetBoolean());
            Assert.True(entry.GetProperty("hashVerified").GetBoolean());
            Assert.Contains(Role.ToString(), entry.GetProperty("roleVisibility").EnumerateArray().Select(item => item.GetString()));
            Assert.Equal(entry.GetProperty("byteCount").GetInt64(), File.ReadAllBytes(path).LongLength);
            Assert.Equal(entry.GetProperty("sha256").GetString(), WorkerContextArtifact.Hash(File.ReadAllBytes(path)));
            return File.ReadAllText(path);
        }

        private byte[] ReadInlineDelivered(string identity)
        {
            var section = Assert.Single(Receipt.Sections, candidate => candidate.LogicalIdentity == identity);
            Assert.Contains(Role, section.RoleVisibility);
            Assert.Equal(ContextDeliveryMode.InlineFull, section.DeliveryMode);
            AssertPromptReceipt(section);
            var lines = Prompt.ReplaceLineEndings("\n").Split('\n');
            var index = Array.FindIndex(lines, line => line.StartsWith($"INLINE FULL: identity={identity};", StringComparison.Ordinal));
            Assert.True(index >= 0 && index + 1 < lines.Length, $"Missing inline delivery: {identity}");
            Assert.Contains("encoding=utf8-json", lines[index], StringComparison.Ordinal);
            var bytes = WorkerContextPackageBuilder.RecoverInlineBytes(lines[index + 1], "utf8-json");
            Assert.Equal(section.ByteCount, bytes.Length);
            Assert.Equal(section.ContentHash, WorkerContextArtifact.Hash(bytes));
            return bytes;
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
                    else if (TargetToolchainDetector.Detect(WorkingDirectory) is Toolchain.Node or Toolchain.Python or Toolchain.Unknown)
                        Assert.Contains("Report each command and its result in WORKER_RESULT", ReadProjection(name), StringComparison.Ordinal);
                    else
                        Assert.Contains("Build changed projects with the worker build check.", Prompt, StringComparison.Ordinal);
                }
            }
            else if (name == "diff-summary.md")
            {
                // WorkerProfileDispatcher explicitly classifies this as a DomainProjection.
                // Keep direct delivery mandatory for all other artifacts except current-task.md.
                Assert.DoesNotContain(Receipt.Sections, section => section.LogicalIdentity == "context/diff-summary.md");
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
