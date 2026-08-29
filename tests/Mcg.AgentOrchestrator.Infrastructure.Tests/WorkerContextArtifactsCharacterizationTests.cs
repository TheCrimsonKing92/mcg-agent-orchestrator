using System.Diagnostics;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class WorkerContextArtifactsCharacterizationTests(Xunit.ITestOutputHelper output)
{
    [Xunit.Fact(DisplayName = "WorkerContextArtifacts_facade_matches_extracted_collaborator_outputs")]
    public void WorkerContextArtifactsFacadeMatchesExtractedCollaboratorOutputs()
    {
        var workingDirectory = CreateRepresentativeRepository();
        var priorTask = new TaskSpec(
            new TaskId("prior-task"),
            "Implement FeatureService baseline.",
            AgentRole.Developer,
            "Run FeatureService tests.");
        var currentTask = new TaskSpec(
            new TaskId("current-task"),
            "Update FeatureService behavior and worker context source survey.",
            AgentRole.Developer,
            "Run focused FeatureService tests and inspect source-survey.md.");
        var goal = new AgentOrchestratorKernel().CreateGoal(
            new GoalId("context-goal"),
            "Improve FeatureService worker context packaging with source survey and skill selection.",
            [priorTask, currentTask]);
        var kernel = new AgentOrchestratorKernel();
        goal = kernel.CreateGoal(goal.Id, goal.Objective, [priorTask, currentTask]);
        kernel.ReportTaskProgress(goal.Id, priorTask.Id, WorkTaskStatus.Completed, "Done.");
        kernel.RecordTaskVerification(goal.Id, priorTask.Id, new TaskVerificationRecord(
            "dotnet test --filter FeatureService",
            workingDirectory,
            0,
            $"""
            WORKER_RESULT:
            files: src/Feature/FeatureService.cs, tests/Feature.Tests/FeatureServiceTests.cs
            commands: dotnet test --filter FeatureService
            tests: pass focused FeatureService tests
            commit: none
            blockers: none
            model_fit: OpenAI/{AgentCatalog.OpenAiSubscriptionModelAlias} - adequate - characterization fixture
            skills: dotnet-windows-build-hygiene
            confidence: high
            END_WORKER_RESULT
            """,
            string.Empty,
            DateTimeOffset.Parse("2026-01-01T00:02:00Z"),
            $"Model fit: OpenAI/{AgentCatalog.OpenAiSubscriptionModelAlias} - adequate - characterization fixture."));
        goal = kernel.GetGoal(goal.Id);
        var preflight = new[] { "profile catalog valid" };

        var contextDirectory = WorkerContextArtifacts.Write(goal, currentTask, workingDirectory, preflight);

        Assert.Equal(
            new WorkerSkillSelector().BuildSelectedSkills(goal, currentTask, workingDirectory),
            File.ReadAllText(Path.Combine(contextDirectory, "selected-skills.md")));
        Assert.Equal(
            new WorkerSourceSurvey().BuildSourceSurvey(goal, currentTask, workingDirectory),
            File.ReadAllText(Path.Combine(contextDirectory, "source-survey.md")));
        Assert.Equal(
            new WorkerGitContext().BuildDiffSummary(workingDirectory),
            File.ReadAllText(Path.Combine(contextDirectory, "diff-summary.md")));
        Assert.Equal(
            WorkerArtifactWriter.BuildManifest(goal, currentTask, workingDirectory, ["AGENTS.md"], preflight),
            File.ReadAllText(Path.Combine(contextDirectory, "manifest.md")));

        var requirements = WorkerContextArtifacts.SelectSkillRequirements(goal, currentTask, workingDirectory);
        Assert.Equal(new WorkerSkillSelector().SelectSkillRequirements(goal, currentTask, workingDirectory), requirements);
        Assert.Contains(requirements, requirement => requirement.Name == "dotnet-windows-build-hygiene" && requirement.Available);
    }

    [Xunit.Fact(DisplayName = "WorkerResultContractParser_extracts_contract_fields")]
    public void WorkerResultContractParserExtractsContractFields()
    {
        var verification = new TaskVerificationRecord(
            "verify",
            CreateTempDirectory(),
            0,
            $"""
            WORKER_RESULT:
            files: src/A.cs, tests/A.cs
            commands: dotnet test
            tests: pass
            commit: none
            blockers: none
            model_fit: OpenAI/{AgentCatalog.OpenAiSubscriptionModelAlias} - adequate - parser characterization
            skills: dotnet-windows-build-hygiene
            confidence: high
            END_WORKER_RESULT
            """,
            string.Empty,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

        var parsed = new WorkerResultContractParser().TryParseWorkerResultContract(verification, out var fields);

        Assert.True(parsed);
        Assert.Equal("src/A.cs, tests/A.cs", fields["files"]);
        Assert.Equal("none", fields["blockers"]);
    }

    [Xunit.Fact]
    public void PriorEvidence_LargeEcho_ProjectsStructuredReceipt()
    {
        var workingDirectory = CreateRepresentativeRepository();
        const string sentinel = "REPLAY-SENTINEL-e4e14983";
        var echoedBody = string.Concat(Enumerable.Repeat(sentinel, 4096));
        var priorTask = new TaskSpec(
            new TaskId("prior-task-large-output"),
            "Implement prior behavior.",
            AgentRole.Developer,
            "Run focused tests.");
        var currentTask = new TaskSpec(
            new TaskId("review-prior-output"),
            "Review prior behavior.",
            AgentRole.Reviewer,
            "Inspect prior evidence.");
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            new GoalId("large-prior-evidence"),
            "Keep replayed worker evidence bounded.",
            [priorTask, currentTask]);
        kernel.ReportTaskProgress(goal.Id, priorTask.Id, WorkTaskStatus.Completed, "Done.");
        var stdout = $"""
            WORKER_RESULT:
            files: {echoedBody}
            commands: focused verification
            tests: fail - assertion error at tests/Feature.Tests/FeatureServiceTests.cs:42
            commit: none
            blockers: exact-blocker - src/Feature/FeatureService.cs:7 contradicts criterion 3
            model_fit: OpenAI/test - adequate - fixture
            skills: verification-before-completion
            confidence: high
            END_WORKER_RESULT
            """;
        var sourceOutputPath = Path.Combine(workingDirectory, "captured-prior-worker.out.log");
        File.WriteAllText(sourceOutputPath, stdout);
        kernel.RecordTaskVerification(goal.Id, priorTask.Id, new TaskVerificationRecord(
            "focused verification",
            workingDirectory,
            1,
            stdout,
            string.Empty,
            DateTimeOffset.Parse("2026-01-01T00:02:00Z"),
            StandardOutputPath: sourceOutputPath,
            FullStandardOutput: stdout));
        goal = kernel.GetGoal(goal.Id);

        var contextDirectory = WorkerContextArtifacts.Write(goal, currentTask, workingDirectory, ["profile valid"]);
        var evidence = File.ReadAllText(Path.Combine(contextDirectory, "prior-task-evidence.md"));
        var brief = new TaskBrief(
            goal.Id,
            currentTask.Id,
            currentTask.RequiredRole,
            currentTask.Description,
            $"# Agent Task Brief{Environment.NewLine}Goal: {goal.Objective}{Environment.NewLine}Goal id: {goal.Id.Value}{Environment.NewLine}Task: {currentTask.Description}{Environment.NewLine}Task role: {currentTask.RequiredRole}{Environment.NewLine}## Instructions{Environment.NewLine}Review the projected prior evidence.");
        var downstreamPackage = WorkerProfileDispatcher.BuildContextPackage(
            goal,
            currentTask,
            workingDirectory,
            contextDirectory,
            brief);
        var downstreamPrompt = WorkerContextPackageBuilder.Render(downstreamPackage);
        var projectedArtifact = Assert.Single(downstreamPackage.Artifacts,
            artifact => artifact.Identity.Value == $"prior/{priorTask.Id.Value}/verification-output");
        var projectedOutput = File.ReadAllText(Path.Combine(
            workingDirectory,
            projectedArtifact.MandatoryRelativePath!.Replace('/', Path.DirectorySeparatorChar)));
        var authoritative = goal.Tasks.Single(task => task.Id == priorTask.Id)
            .LastVerification!.AuthoritativeStandardOutput;

        Assert.False(
            evidence.Contains(sentinel, StringComparison.Ordinal),
            $"Expected replay sentinel to be absent; before_output_chars={stdout.Length}; after_output_chars={evidence.Length}.");
        Assert.Contains("tests: fail - assertion error at tests/Feature.Tests/FeatureServiceTests.cs:42", evidence, StringComparison.Ordinal);
        Assert.Contains("blockers: exact-blocker - src/Feature/FeatureService.cs:7 contradicts criterion 3", evidence, StringComparison.Ordinal);
        Assert.Contains("validation=malformed", evidence, StringComparison.Ordinal);
        Assert.Contains("oversized_fields=files", evidence, StringComparison.Ordinal);
        Assert.Contains($"files: [oversized structured field omitted; chars={echoedBody.Length};", evidence, StringComparison.Ordinal);
        Assert.Contains($"sha256={WorkerContextArtifact.Hash(System.Text.Encoding.UTF8.GetBytes(echoedBody))}", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, downstreamPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, projectedOutput, StringComparison.Ordinal);
        Assert.Contains("tests: fail - assertion error at tests/Feature.Tests/FeatureServiceTests.cs:42", projectedOutput, StringComparison.Ordinal);
        Assert.Contains("blockers: exact-blocker - src/Feature/FeatureService.cs:7 contradicts criterion 3", projectedOutput, StringComparison.Ordinal);
        Assert.Contains($"source_handle={Path.GetFullPath(sourceOutputPath)}", projectedOutput, StringComparison.Ordinal);
        Assert.Contains("context/prior-task-evidence.md", downstreamPrompt, StringComparison.Ordinal);
        Assert.Contains(sentinel, authoritative!, StringComparison.Ordinal);
        Assert.Contains(sentinel, File.ReadAllText(sourceOutputPath), StringComparison.Ordinal);
        output.WriteLine($"before_output_chars={stdout.Length}; after_output_chars={evidence.Length}; reduction_percent={(stdout.Length - evidence.Length) * 100.0 / stdout.Length:F2}; blocker_preserved=true; test_error_preserved=true; source_location_preserved=true; full_output_access=true");
        Assert.True(
            evidence.Length < stdout.Length / 10,
            $"Expected at least 90% output reduction; before_output_chars={stdout.Length}; after_output_chars={evidence.Length}.");
    }

    [Xunit.Fact]
    public void RetryEvidence_LargeEcho_ProjectsStructuredReceipt()
    {
        var workingDirectory = CreateRepresentativeRepository();
        const string sentinel = "RETRY-REPLAY-SENTINEL-e4e14983";
        var echoedBody = string.Concat(Enumerable.Repeat(sentinel + Environment.NewLine, 4096));
        var task = new TaskSpec(
            new TaskId("retry-large-output"),
            "Retry prior implementation.",
            AgentRole.Developer,
            "Preserve the failure evidence.");
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(new GoalId("retry-large-evidence"), "Keep retry evidence bounded.", [task]);
        var stdout = $"""
            {echoedBody}
            WORKER_RESULT:
            files: src/Feature/FeatureService.cs
            commands: focused retry verification
            tests: fail - retry assertion at tests/Feature.Tests/FeatureServiceTests.cs:51
            commit: none
            blockers: exact-blocker - src/Feature/FeatureService.cs:9 conflicts with retry criterion
            model_fit: OpenAI/test - adequate - retry fixture
            skills: verification-before-completion
            confidence: high
            END_WORKER_RESULT
            """;
        var sourceOutputPath = Path.Combine(workingDirectory, "captured-retry-worker.out.log");
        File.WriteAllText(sourceOutputPath, stdout);
        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
            "focused retry verification",
            workingDirectory,
            1,
            stdout,
            string.Empty,
            DateTimeOffset.Parse("2026-01-01T00:02:00Z"),
            StandardOutputPath: sourceOutputPath,
            FullStandardOutput: stdout));
        goal = kernel.GetGoal(goal.Id);
        task = goal.Tasks.Single();

        var contextDirectory = WorkerContextArtifacts.Write(goal, task, workingDirectory, ["profile valid"]);
        var brief = new TaskBrief(
            goal.Id,
            task.Id,
            task.RequiredRole,
            task.Description,
            $"# Agent Task Brief{Environment.NewLine}Goal: {goal.Objective}{Environment.NewLine}Goal id: {goal.Id.Value}{Environment.NewLine}Task: {task.Description}{Environment.NewLine}Task role: {task.RequiredRole}{Environment.NewLine}## Instructions{Environment.NewLine}Inspect the projected retry evidence.");
        var package = WorkerProfileDispatcher.BuildContextPackage(
            goal,
            task,
            workingDirectory,
            contextDirectory,
            brief);
        var prompt = WorkerContextPackageBuilder.Render(package);
        var projectedArtifact = Assert.Single(package.Artifacts,
            artifact => artifact.Identity.Value == "task/last-verification/stdout");
        var projectedOutput = File.ReadAllText(Path.Combine(
            workingDirectory,
            projectedArtifact.MandatoryRelativePath!.Replace('/', Path.DirectorySeparatorChar)));
        var legacyInlineArtifact = WorkerContextArtifact.Create(
            projectedArtifact.Identity,
            projectedArtifact.Kind,
            Encoding.UTF8.GetBytes(stdout),
            projectedArtifact.RoleVisibility,
            ContextDeliveryMode.InlineFull,
            projectedArtifact.ContractVersion,
            fallbackReason: "missing");
        var legacyPrompt = WorkerContextPackageBuilder.Render(new WorkerContextPackage(
            "legacy-inline-fallback",
            ContextContractVersion.V1,
            task.RequiredRole,
            [legacyInlineArtifact]));
        var projectedPrompt = WorkerContextPackageBuilder.Render(new WorkerContextPackage(
            package.SemanticPackageId,
            ContextContractVersion.V1,
            task.RequiredRole,
            [projectedArtifact]));

        Assert.Contains(sentinel, legacyPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, projectedPrompt, StringComparison.Ordinal);
        Assert.Contains($"identity={projectedArtifact.Identity.Value}", projectedPrompt, StringComparison.Ordinal);
        Assert.Contains($"path={projectedArtifact.MandatoryRelativePath}", projectedPrompt, StringComparison.Ordinal);
        Assert.Contains($"sha256={projectedArtifact.ContentHash}", projectedPrompt, StringComparison.Ordinal);
        Assert.Contains("validation=verified", projectedPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, projectedOutput, StringComparison.Ordinal);
        Assert.Contains("tests: fail - retry assertion at tests/Feature.Tests/FeatureServiceTests.cs:51", projectedOutput, StringComparison.Ordinal);
        Assert.Contains("blockers: exact-blocker - src/Feature/FeatureService.cs:9 conflicts with retry criterion", projectedOutput, StringComparison.Ordinal);
        Assert.Contains($"source_handle={Path.GetFullPath(sourceOutputPath)}", projectedOutput, StringComparison.Ordinal);
        Assert.Contains(sentinel, task.LastVerification!.AuthoritativeStandardOutput!, StringComparison.Ordinal);
        Assert.Contains(sentinel, File.ReadAllText(sourceOutputPath), StringComparison.Ordinal);
        output.WriteLine($"before_prompt_chars={legacyPrompt.Length}; after_prompt_chars={projectedPrompt.Length}; prompt_reduction_percent={(legacyPrompt.Length - projectedPrompt.Length) * 100.0 / legacyPrompt.Length:F2}");
        Assert.True(
            projectedPrompt.Length < legacyPrompt.Length / 10,
            $"Expected at least 90% retry prompt reduction; before_prompt_chars={legacyPrompt.Length}; after_prompt_chars={projectedPrompt.Length}.");
        output.WriteLine($"retry_before_output_chars={stdout.Length}; retry_after_output_chars={projectedOutput.Length}; reduction_percent={(stdout.Length - projectedOutput.Length) * 100.0 / stdout.Length:F2}; blocker_preserved=true; test_error_preserved=true; source_location_preserved=true; full_output_access=true");
        Assert.True(
            projectedOutput.Length < stdout.Length / 10,
            $"Expected at least 90% retry output reduction; before_output_chars={stdout.Length}; after_output_chars={projectedOutput.Length}.");
    }

    [Xunit.Fact(DisplayName = "WorkerResultContractParser_findings_use_structured_blockers_token")]
    public void WorkerResultContractParserFindingsUseStructuredBlockersToken()
    {
        var task = new TaskSpec(
            new TaskId("prior-task"),
            "Implement parser behavior.",
            AgentRole.Developer,
            "Run parser tests.");
        var verification = new TaskVerificationRecord(
            "verify",
            CreateTempDirectory(),
            0,
            $"""
            WORKER_RESULT:
            files: src/A.cs
            commands: dotnet test
            tests: pass - prior retry mentioned failed and timed out prose
            commit: abc123
            blockers: none - no blockers remain
            model_fit: OpenAI/{AgentCatalog.OpenAiSubscriptionModelAlias} - adequate - parser characterization
            skills: dotnet-windows-build-hygiene
            confidence: high
            END_WORKER_RESULT
            """,
            string.Empty,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var lines = new List<string>();

        new WorkerResultContractParser().AddWorkerResultContractFindings(lines, task, verification);

        Assert.Contains(lines, line => line.Contains("reported WORKER_RESULT contract", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("reported advisory blockers", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("has no test evidence", StringComparison.Ordinal));
    }

    private static string CreateRepresentativeRepository()
    {
        var root = CreateTempDirectory();
        RunGit(root, ["init"], DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        RunGit(root, ["config", "user.email", "tests@example.com"], DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        RunGit(root, ["config", "user.name", "Context Tests"], DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        Directory.CreateDirectory(Path.Combine(root, "src", "Feature"));
        Directory.CreateDirectory(Path.Combine(root, "tests", "Feature.Tests"));
        Directory.CreateDirectory(Path.Combine(root, ".agents", "skills", "dotnet-windows-build-hygiene"));
        Directory.CreateDirectory(Path.Combine(root, ".agents", "skills", "verification-before-completion"));
        File.WriteAllText(Path.Combine(root, "AGENTS.md"), "Repository guidance.");
        File.WriteAllText(Path.Combine(root, ".agents", "skills", "dotnet-windows-build-hygiene", "SKILL.md"), "Build hygiene.");
        File.WriteAllText(Path.Combine(root, ".agents", "skills", "verification-before-completion", "SKILL.md"), "Completion verification.");
        File.WriteAllText(Path.Combine(root, "src", "Feature", "FeatureService.cs"), "public sealed class FeatureService { public int Version => 1; }");
        File.WriteAllText(Path.Combine(root, "tests", "Feature.Tests", "FeatureServiceTests.cs"), "public sealed class FeatureServiceTests {}");
        RunGit(root, ["add", "-A"], DateTimeOffset.Parse("2026-01-01T00:01:00Z"));
        RunGit(root, ["commit", "-m", "Seed feature"], DateTimeOffset.Parse("2026-01-01T00:01:00Z"));
        File.WriteAllText(Path.Combine(root, "src", "Feature", "FeatureService.cs"), "public sealed class FeatureService { public int Version => 2; }");
        return root;
    }

    private static void RunGit(string workingDirectory, string[] arguments, DateTimeOffset commitTime)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };
        startInfo.Environment["GIT_AUTHOR_DATE"] = commitTime.ToString("O");
        startInfo.Environment["GIT_COMMITTER_DATE"] = commitTime.ToString("O");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git.");
        process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(60000);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
        }
    }
}
