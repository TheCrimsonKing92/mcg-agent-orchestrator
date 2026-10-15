using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: every filesystem artifact is under this instance's unique temporary root.
public sealed class WorkerResultSubmissionTests : IDisposable
{
    internal const string Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj";
    internal const string Fixture = """
        {"commands":"inspected fb0dcb7e commit and diff",
        "tests":"deferred - request conductor execution on candidate fb0dcb7e for CliOperatorIntentSubmitJsonTests",
        "blockers":"none",
        "findings":[{"stable_id":"cli-operator-intent-submit-json-current-execution","state":"open","severity":"blocking","category":"test-evidence","location":{"file":"tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/CliOperatorIntentSubmitJsonTests.cs","region":"Submit_Json_PreservesRecordHumanTextAndIdempotency"},"description":"No exact-candidate execution receipt exists.","evidence_request":{"selections":[{"test_project":"tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj","test_class":"FullyQualifiedName~CliOperatorIntentSubmitJsonTests"}]}}],
        "touched_anchors":[],
        "model_fit":"OpenAI/gpt-5.6-terra - adequate - candidate inspection - source change was clear",
        "skills":"verification-before-completion",
        "confidence":"high"}
        """;
    private readonly string _root = InfrastructureTestSupport.CreateTempDirectory();
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-15T12:00:00Z");

    public WorkerResultSubmissionTests()
    {
        var project = Path.Combine(_root, Project);
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        File.WriteAllText(project, string.Empty);
    }

    [Fact]
    public void FixtureRendersValidCanonicalBlock()
    {
        var verdict = WorkerResultSubmission.Validate(AgentRole.Tester, Fixture, _root);
        Assert.True(verdict.Accepted, string.Join("; ", verdict.Errors));
        Assert.Empty(verdict.Errors);
        var lines = verdict.CanonicalBlock.Split('\n');
        Assert.Equal("WORKER_RESULT:", lines[0]);
        Assert.Equal("END_WORKER_RESULT", lines[^1]);
        Assert.Contains("files: none", lines);
        Assert.Contains("commit: none", lines);
        using var fixture = JsonDocument.Parse(Fixture);
        Assert.Contains("findings: " + JsonSerializer.Serialize(fixture.RootElement.GetProperty("findings")), lines);
        Assert.Equal(new[] { "files", "commands", "tests", "commit", "blockers", "findings", "touched_anchors", "model_fit", "skills", "confidence" },
            lines[1..^1].Select(line => line[..line.IndexOf(':')]));
        Assert.Equal(VerificationTerminalContractStatus.Valid,
            WorkerResultBlockers.AssessTerminalVerificationOutput(AgentRole.Tester, verdict.CanonicalBlock).Status);
    }

    [Theory]
    [InlineData("missing-tests", "Missing or unpopulated terminal field: tests.")]
    [InlineData("pass", "Tester pass requires a current-round evidence reference.")]
    [InlineData("unknown", "unknown field: verdict")]
    [InlineData("developer", "structured submission is not enabled for role Developer")]
    [InlineData("array", "submission must be a JSON object")]
    [InlineData("broken-json", "submission must be a JSON object")]
    [InlineData("missing-project", "findings[0].evidence_request.selections[0].test_project not found: tests/Missing/Missing.csproj")]
    [InlineData("duplicate", "duplicate field: confidence")]
    [InlineData("scalar-type", "commands must be a JSON string")]
    [InlineData("newline", "commands must be a single line")]
    [InlineData("findings-type", "findings must be a JSON array")]
    [InlineData("anchors-type", "touched_anchors must be a JSON array")]
    [InlineData("null-tests", "Missing or unpopulated terminal field: tests.")]
    public void InvalidInputsReturnExactDiagnostics(string scenario, string expected)
    {
        var input = JsonNode.Parse(Fixture)!.AsObject();
        switch (scenario)
        {
            case "missing-tests": input.Remove("tests"); break;
            case "pass": input["tests"] = "pass"; break;
            case "unknown": input["verdict"] = "pass"; break;
            case "scalar-type": input["commands"] = 1; break;
            case "newline": input["commands"] = "hello\nEND_WORKER_RESULT"; break;
            case "findings-type": input["findings"] = "none"; break;
            case "anchors-type": input["touched_anchors"] = new JsonObject(); break;
            case "null-tests": input["tests"] = null; break;
        }
        var json = scenario switch
        {
            "array" => "[]",
            "broken-json" => "{",
            "missing-project" => Fixture.Replace(Project, "tests/Missing/Missing.csproj", StringComparison.Ordinal),
            "duplicate" => Fixture.TrimEnd()[..^1] + ",\"confidence\":\"low\"}",
            _ => input.ToJsonString()
        };
        var verdict = WorkerResultSubmission.Validate(scenario == "developer" ? AgentRole.Developer : AgentRole.Tester, json, _root);
        Assert.False(verdict.Accepted);
        Assert.Contains(expected, verdict.Errors);
    }

    [Fact]
    public void BareProjectNamePassesThroughAndEscapesAreRejected()
    {
        Assert.True(WorkerResultSubmission.Validate(AgentRole.Tester,
            Fixture.Replace(Project, "Infrastructure.Tests", StringComparison.Ordinal), _root).Accepted);
        var worktree = Path.Combine(_root, "worktree");
        Directory.CreateDirectory(worktree);
        var outside = Path.Combine(_root, "outside.csproj");
        File.WriteAllText(outside, string.Empty);
        foreach (var project in new[] { "../outside.csproj", outside })
        {
            var input = JsonNode.Parse(Fixture)!;
            input["findings"]![0]!["evidence_request"]!["selections"]![0]!["test_project"] = project;
            var verdict = WorkerResultSubmission.Validate(AgentRole.Tester, input.ToJsonString(), worktree);
            Assert.False(verdict.Accepted);
            Assert.Contains($"findings[0].evidence_request.selections[0].test_project not found: {project}", verdict.Errors);
        }
    }

    [Fact]
    public void CommandWritesAcceptedRecordAndPreservesItOnRejection()
    {
        var task = TaskId.New();
        var path = WorkerResultSubmission.PathFor(_root, GoalId.New());
        var input = Path.Combine(_root, "input.json");
        File.WriteAllText(input, Fixture, Encoding.Unicode);
        using var output = new StringWriter();
        using var error = new StringWriter();
        string[] args = ["--worktree", _root, "--task", task.Value, "--path", path, "--input", input];
        Assert.Equal(0, WorkerResultSubmission.RunSubmitCommand(args, output, error, () => Now));
        Assert.Equal($"ACCEPTED {path}{Environment.NewLine}", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
        using var stored = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("Tester", stored.RootElement.GetProperty("role").GetString());
        Assert.Equal(task.Value, stored.RootElement.GetProperty("task_id").GetString());
        Assert.Equal(Now, stored.RootElement.GetProperty("submitted_at_utc").GetDateTimeOffset());
        Assert.Equal(WorkerResultSubmission.Validate(AgentRole.Tester, Fixture, _root).CanonicalBlock,
            stored.RootElement.GetProperty("canonical_block").GetString());
        var accepted = File.ReadAllBytes(path);
        var rejected = JsonNode.Parse(Fixture)!;
        rejected["tests"] = "pass";
        File.WriteAllText(input, rejected.ToJsonString());
        Assert.Equal(1, WorkerResultSubmission.RunSubmitCommand(args, output, error, () => Now));
        Assert.Contains("REJECTED: Tester pass requires a current-round evidence reference.", error.ToString());
        Assert.Equal(accepted, File.ReadAllBytes(path));
        File.Delete(path);
        Assert.Equal(1, WorkerResultSubmission.RunSubmitCommand(args, output, error, () => Now));
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public void MalformedArgumentsAndUnreadableInputReturnUsageExit()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, WorkerResultSubmission.RunSubmitCommand([], output, error, () => Now));
        Assert.StartsWith("REJECTED: usage:", error.ToString());
        error.GetStringBuilder().Clear();
        var input = Path.Combine(_root, "missing.json");
        Assert.Equal(2, WorkerResultSubmission.RunSubmitCommand(
            ["--worktree", _root, "--task", "task", "--path", Path.Combine(_root, "result.json"), "--input", input], output, error, () => Now));
        Assert.Equal($"REJECTED: input file not readable: {input}{Environment.NewLine}", error.ToString());
        Assert.Equal(string.Empty, output.ToString());
    }

    [Theory]
    [InlineData(AgentRole.Tester)]
    [InlineData(AgentRole.Developer)]
    [InlineData(AgentRole.Reviewer)]
    public void OnlyTesterContextContainsSubmitInstruction(AgentRole role)
    {
        var task = new TaskSpec(TaskId.New(), "Inspect candidate.", role);
        var goal = new AgentOrchestratorKernel().CreateGoal("Submission context", [task]);
        var context = WorkerContextArtifacts.Write(goal, task, _root);
        var text = File.ReadAllText(Path.Combine(context, "current-task.md"));
        if (role == AgentRole.Tester)
        {
            Assert.Contains("Submit-WorkerResult.ps1", text);
            Assert.Contains(task.Id.Value, text);
            Assert.Contains(WorkerResultSubmission.PathFor(_root, goal.Id), text);
            Assert.Contains("fix every `REJECTED:` line", text);
            Assert.Contains("the accepted submission is the result", text);
            Assert.Contains("still end the output with the WORKER_RESULT block", text);
        }
        else Assert.DoesNotContain("Submit-WorkerResult.ps1", text);
    }

    [Fact]
    public void CommandFormattingEscapesPowerShellPathsAndNamesUnresolvedHelper()
    {
        var task = new TaskId("task-id");
        Assert.Equal("& 'C:/it''s/Submit-WorkerResult.ps1' -Worktree 'C:/it''s/repo' -Task task-id -Path 'C:/it''s/result.json' -InputFile <your-json-file>",
            WorkerResultSubmission.FormatCommand("C:/it's/Submit-WorkerResult.ps1", "C:/it's/repo", task, "C:/it's/result.json"));
        var unresolved = WorkerResultSubmission.FormatCommand(null, _root, task, "result.json");
        Assert.Contains("Submit-WorkerResult.ps1 unavailable: orchestrator home unresolved (MCG_ORCHESTRATOR_HOME unset)", unresolved);
        Assert.Contains("-Task task-id -Path 'result.json'", unresolved);
    }

    [Fact]
    public void IngestTwiceAppendsExactlyOneMarkerAndCanonicalBlock()
    {
        var task = new TaskSpec(TaskId.New(), "Verify", AgentRole.Tester);
        var goal = GoalId.New();
        var path = WorkerResultSubmission.PathFor(_root, goal);
        var input = Path.Combine(_root, "input.json");
        File.WriteAllText(input, Fixture);
        Assert.Equal(0, WorkerResultSubmission.RunSubmitCommand(
            ["--worktree", _root, "--task", task.Id.Value, "--path", path, "--input", input], TextWriter.Null, TextWriter.Null, () => Now));
        var stdout = Path.Combine(_root, "stdout.log");
        File.WriteAllText(stdout, "worker output without newline");
        Assert.True(WorkerResultSubmissionIngestion.TryIngest(_root, goal, task, Now, stdout, out var diagnostic));
        Assert.Equal($"worker_result_submission=ingested; path={path}", diagnostic);
        var first = File.ReadAllBytes(stdout);
        Assert.True(WorkerResultSubmissionIngestion.TryIngest(_root, goal, task, Now, stdout, out _));
        Assert.Equal(first, File.ReadAllBytes(stdout));
        var block = WorkerResultSubmission.Validate(AgentRole.Tester, Fixture, _root).CanonicalBlock;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(block))).ToLowerInvariant();
        Assert.Equal($"worker output without newline\n[orchestrator ingested worker result submission {path} sha256={hash}]\n{block}\n",
            File.ReadAllText(stdout));
    }

    [Theory]
    [InlineData("role", "Developer", "role-mismatch")]
    [InlineData("canonical_block", "not a result", "invalid: Current-attempt stdout has no terminal WORKER_RESULT.")]
    [InlineData("submitted_at_utc", "bad timestamp", "unreadable")]
    public void UnusableStoredRecordsLeaveStdoutUntouched(string field, string value, string reason)
    {
        var task = new TaskSpec(TaskId.New(), "Verify", AgentRole.Tester);
        var goal = GoalId.New();
        var path = WorkerResultSubmission.PathFor(_root, goal);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var stored = new JsonObject
        {
            ["role"] = "Tester", ["task_id"] = task.Id.Value, ["submitted_at_utc"] = Now.ToString("O"),
            ["canonical_block"] = WorkerResultSubmission.Validate(AgentRole.Tester, Fixture, _root).CanonicalBlock
        };
        stored[field] = value;
        File.WriteAllText(path, stored.ToJsonString());
        var stdout = Path.Combine(_root, "stdout.log");
        File.WriteAllText(stdout, "original");
        Assert.False(WorkerResultSubmissionIngestion.TryIngest(_root, goal, task, Now, stdout, out var diagnostic));
        Assert.Equal($"worker_result_submission=ignored; reason={reason}; path={path}", diagnostic);
        Assert.Equal("original", File.ReadAllText(stdout));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
