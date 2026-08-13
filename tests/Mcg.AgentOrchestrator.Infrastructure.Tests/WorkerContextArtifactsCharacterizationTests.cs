using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("ProcessSpawning")]
public sealed class WorkerContextArtifactsCharacterizationTests
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
