using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: stores, child environments, console capture and failure injection are fixture-local.
public sealed class GoalCreationSummaryTests : CliCommandTestBase
{
    [Theory]
    [InlineData("--brief-file")]
    [InlineData("--text-file")]
    public async Task RealCliSummaryOmitsLongBodyAndPreservesDurableScoutData(string inputFlag)
    {
        using var fixture = new Fixture(this);
        var workspace = fixture.Workspace;
        Directory.CreateDirectory(Path.Combine(fixture.Root, "src"));
        File.WriteAllText(Path.Combine(fixture.Root, "src", "Receipt.cs"), "// fixture");
        const string marker = "DISTINCTIVE_BRIEF_BODY_709d6f";
        var objective = "Update src/Receipt.cs with compact receipts. Read SKILL.md for café 東京. Ask the operator to login to GitHub.\n\n" +
            marker + "\n" + string.Concat(Enumerable.Repeat("Preserve the complete input in durable state.\n", 300));
        var brief = Path.Combine(fixture.Root, "brief.txt");
        File.WriteAllText(brief, objective);
        var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
        var epic = await portfolio.AddEpicAsync("Receipt Board");
        var backlog = new BacklogStore(workspace.BacklogStorePath);
        var source = await backlog.AddAsync("Receipt source", "Update src/Receipt.cs.");
        await backlog.CloseAsync(source.Id, "Already closed fixture");
        var agents = AgentCatalog.Default().Agents;
        var developer = agents.First(agent => agent.Role == AgentRole.Developer);

        // An active overlapping goal makes warning preservation observable.
        var seed = new AgentOrchestratorKernel();
        var existing = seed.CreateGoal("Update src/Receipt.cs", [new TaskSpec(TaskId.New(), "Existing work", AgentRole.Developer)]);
        seed.ActivateGoal(existing.Id, agents);
        await fixture.Repository.SaveAsync(seed);

        var result = await RunIsolatedApp(fixture, "goal", inputFlag, brief, "--summary", "--pipeline", "scout",
            "--developer", developer.Id.Value, "--epic", epic.Id, "--backlog-item", source.Id, "--backlog-coverage", "slice",
            "--request-key", "long-brief");

        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        var state = await fixture.Repository.LoadAsync();
        Assert.Equal(2, state.Goals.Count);
        var goal = Assert.Single(state.Goals, goal => goal.Id != existing.Id);
        Assert.Equal(objective.Trim(), goal.Objective);
        Assert.Contains(marker, goal.Objective);
        Assert.Equal([AgentRole.Planner, AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer],
            goal.Tasks.Select(task => task.RequiredRole));
        Assert.Equal(developer.Id, goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer).AssignedAgentId);
        Assert.Contains("Outcome: created", result.Stdout);
        Assert.Contains($"Goal: {goal.Id.Value}", result.Stdout);
        Assert.Contains($"Status: {goal.Status}", result.Stdout);
        Assert.Contains("Pipeline: scout", result.Stdout);
        foreach (var task in goal.Tasks)
            Assert.Contains($"{task.Id.Value} [{task.Status}] {task.RequiredRole}:", result.Stdout);
        Assert.Equal(source.Id, goal.SourceBacklogItemId);
        Assert.Equal(SourceBacklogCoverage.Slice, goal.SourceBacklogCoverage);
        Assert.Equal(epic.Id, (await portfolio.GetGoalMembershipAsync(goal.Id.Value))!.EpicId);
        Assert.Contains($"Epic: {epic.Id}", result.Stdout);
        Assert.Contains($"Source backlog: {source.Id}", result.Stdout);
        Assert.Contains("Scope collision advisory:", result.Stdout);
        Assert.Contains(existing.Id.Value, result.Stdout);
        Assert.Contains("BRIEF-LINT", result.Stdout);
        foreach (var finding in BriefLint.Lint(objective))
            Assert.Contains($"BRIEF-LINT {finding.SeverityToken} {finding.Kind}: {finding.Message} remedy: {finding.Remedy}", result.Stdout);
        Assert.Contains($"Warning: linked backlog item {source.Id} is already Done; goal creation will continue but may recreate landed work.", result.Stdout);
        foreach (var warning in GoalObjectivePlanner.BuildCapabilityWarnings(objective))
            Assert.Contains(warning, result.Stdout);
        Assert.NotEmpty(GoalObjectivePlanner.BuildCapabilityWarnings(objective));
        Assert.DoesNotContain(marker, result.Stdout);
        Assert.DoesNotContain("Goal objective plan:", result.Stdout);
        Assert.DoesNotContain("\"taskBoundaries\"", result.Stdout);
        Assert.True(result.Stdout.Length < objective.Length, "The receipt echoed the long input.");
    }

    [Fact]
    public async Task DefaultOutputStillContainsThePlanAndGoal()
    {
        using var fixture = new Fixture(this);
        const string objective = "Update the receipt formatter with a focused regression assertion.";
        var result = await RunIsolatedApp(fixture, "goal", objective, "--pipeline", "scout");
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        var goal = Assert.Single((await fixture.Repository.LoadAsync()).Goals);
        Assert.Contains("Goal objective plan:", result.Stdout);
        Assert.Contains($"\"objective\":{JsonSerializer.Serialize(objective)}", result.Stdout);
        Assert.Contains($"Goal {goal.Id.Value}", result.Stdout);
        Assert.DoesNotContain("Outcome: created", result.Stdout);
    }

    [Fact]
    public void GoalHelpAdvertisesSummaryAndItsRestrictions()
    {
        var output = CaptureConsole(() => CliCommandHelp.TryPrintStartupHelp(["goal", "--help"]));
        Assert.Contains("--summary", output);
        Assert.Contains("standalone", output);
    }

    [Theory]
    [InlineData("goal", "--simple")]
    [InlineData("goal", "--run")]
    [InlineData("goal", "--from-backlog")]
    [InlineData("goal", "--json")]
    [InlineData("goal", "--unknown-option")]
    [InlineData("simple-goal", "")]
    [InlineData("lifecycle-goal", "")]
    [InlineData("lifecycle-simple-goal", "")]
    [InlineData("run-goal", "")]
    [InlineData("backlog-intake", "")]
    [InlineData("plan", "")]
    public async Task InvalidSummaryOptionsFailBeforeReservationOrMutation(string command, string option)
    {
        using var fixture = new Fixture(this);
        var args = new List<string> { command, "Update receipt formatting", "--summary", "--request-key", "invalid" };
        if (option.Length > 0) args.Add(option);
        var before = File.ReadAllBytes(fixture.Workspace.SqliteStatePath);
        Assert.Throws<ArgumentException>(() => Execute(fixture, args));
        Assert.Equal(before, File.ReadAllBytes(fixture.Workspace.SqliteStatePath));
        Assert.Empty((await fixture.Repository.LoadAsync()).Goals);
        Assert.Null(new GoalIntakeRequestStore(fixture.Workspace.SqliteStatePath).Get("invalid"));
        Assert.False(File.Exists(fixture.Workspace.PortfolioStorePath));
    }

    [Fact]
    public void UnknownGoalOptionWithoutSummaryAlsoFailsBeforeReservation()
    {
        using var fixture = new Fixture(this);
        Assert.Throws<ArgumentException>(() => Execute(fixture,
            ["goal", "Update receipt formatting.", "--unknown-option", "--request-key", "unknown"]));
        Assert.Empty(fixture.Repository.LoadAsync().GetAwaiter().GetResult().Goals);
        Assert.Null(new GoalIntakeRequestStore(fixture.Workspace.SqliteStatePath).Get("unknown"));
    }

    [Fact]
    public async Task SummaryRetainsExistingStandaloneDependencyAndAutonomyOptions()
    {
        using var fixture = new Fixture(this);
        Execute(fixture, ["goal", "Update prerequisite formatting.", "--pipeline", "scout"]);
        var prerequisite = Assert.Single((await fixture.Repository.LoadAsync()).Goals);
        var output = Execute(fixture, ["goal", "Update dependent receipt formatting.", "--summary", "--pipeline", "scout",
            "--depends-on", prerequisite.Id.Value, "--autonomy", "supervised-auto"]);
        var goal = Assert.Single((await fixture.Repository.LoadAsync()).Goals, goal => goal.Id != prerequisite.Id);
        Assert.Contains(prerequisite.Id, goal.DependsOn);
        Assert.Contains($"Goal: {goal.Id.Value}", output);
        Assert.Contains("Outcome: created", output);
        Assert.DoesNotContain("Goal objective plan:", output);
    }

    [Fact]
    public async Task ReplayUsesCurrentDurableGoalAndDoesNotChangeRequestIdentity()
    {
        using var fixture = new Fixture(this);
        string[] args = ["goal", "Update the compact receipt formatter.", "--pipeline", "scout", "--request-key", "replay"];
        Execute(fixture, args);
        var state = await fixture.Repository.LoadAsync();
        var goal = Assert.Single(state.Goals);
        var ids = goal.Tasks.Select(task => task.Id).ToArray();
        state.ParkGoal(goal.Id, "Exercise current replay status");
        await fixture.Repository.SaveAsync(state);
        var portfolio = new PortfolioStore(fixture.Workspace.PortfolioStorePath);
        var epic = await portfolio.AddEpicAsync("Assigned after creation");
        await portfolio.AssignGoalToEpicAsync(goal.Id.Value, epic.Id);
        var output = Execute(fixture, [..args, "--summary"]);
        Assert.Contains("Outcome: replayed", output);
        Assert.Contains($"Goal: {goal.Id.Value}", output);
        Assert.Contains($"Status: {goal.Status}", output);
        Assert.Contains("Pipeline: scout", output);
        Assert.Contains($"Epic: {epic.Id}", output);
        var restored = Assert.Single((await fixture.Repository.LoadAsync()).Goals);
        Assert.Equal(ids, restored.Tasks.Select(task => task.Id));
        foreach (var task in restored.Tasks)
            Assert.Contains($"{task.Id.Value} [{task.Status}] {task.RequiredRole}:", output);
        Assert.DoesNotContain("Goal objective plan:", output);
        Assert.DoesNotContain("Outcome: created", output);
    }

    [Theory]
    [InlineData("still-committing")]
    [InlineData("failed")]
    [InlineData("created")]
    public async Task IncompleteOrInvalidReplayNeverClaimsSuccess(string state)
    {
        using var fixture = new Fixture(this);
        string[] args = ["goal", "Update receipt formatting.", "--pipeline", "scout", "--summary", "--request-key", "incomplete"];
        var request = GoalIntakeRequestMapper.Map(args, fixture.Workspace, AgentCatalog.Default().Agents)!;
        var store = new GoalIntakeRequestStore(fixture.Workspace.SqliteStatePath);
        store.Reserve(request.RequestKey, request.Fingerprint);
        if (state == "failed") store.MarkFailed(request.RequestKey, request.Fingerprint, "fixture-failure", "fixture detail");
        if (state == "created")
        {
            // Commit a deliberately dangling receipt through the required state-write session.
            await fixture.Repository.TransactAsync<bool>((kernel, cancellationToken) =>
            {
                store.MarkCreated(request.RequestKey, request.Fingerprint, GoalId.New().Value);
                return Task.FromResult((ShouldSave: true, Result: true));
            });
        }
        Assert.Equal(state, store.Get(request.RequestKey)!.State);
        Assert.Empty((await fixture.Repository.LoadAsync()).Goals);
        using var output = new StringWriter();
        var error = Record.Exception(() => AsyncLocalConsoleRouter.Out.CaptureLocal(() => Execute(fixture, args, capture: false), output));
        if (state == "still-committing")
        {
            Assert.Null(error);
            Assert.Contains("Outcome: still-committing", output.ToString());
        }
        else
        {
            Assert.IsType<InvalidOperationException>(error);
            Assert.Contains(state == "failed" ? "GOAL_INTAKE_REPLAY_FAILED" : "GOAL_INTAKE_RECEIPT_INVALID", error.Message);
        }
        Assert.DoesNotContain("Outcome: created", output.ToString());
        Assert.DoesNotContain("Outcome: replayed", output.ToString());
        Assert.DoesNotContain("Goal:", output.ToString());
    }

    [Fact]
    public void BlockedSummaryDoesNotInventDurableIdentity()
    {
        using var fixture = new Fixture(this);
        using var output = new StringWriter();
        var error = Assert.Throws<InvalidOperationException>(() => AsyncLocalConsoleRouter.Out.CaptureLocal(
            () => Execute(fixture, ["goal", "fix it", "--summary"], capture: false), output));
        Assert.Contains("needs clarification", error.Message);
        Assert.Contains("Outcome: blocked", output.ToString());
        Assert.DoesNotContain("Goal:", output.ToString());
        Assert.DoesNotContain("Goal objective plan:", output.ToString());
        Assert.Empty(fixture.Repository.LoadAsync().GetAwaiter().GetResult().Goals);
    }

    [Fact]
    public void CommitFailurePrintsNoAppliedReceipt()
    {
        using var fixture = new Fixture(this);
        var repository = new InMemoryTransactionalStateRepository(new AgentOrchestratorKernel())
        {
            BeforeSaveCommit = _ => throw new InvalidOperationException("fixture commit rejected")
        };
        using var output = new StringWriter();
        var error = Assert.Throws<InvalidOperationException>(() => AsyncLocalConsoleRouter.Out.CaptureLocal(() =>
            Execute(fixture, ["goal", "Update receipt formatting.", "--summary", "--pipeline", "scout"], repository, capture: false), output));
        Assert.Contains("fixture commit rejected", error.Message);
        Assert.DoesNotContain("Outcome: created", output.ToString());
        Assert.DoesNotContain("Goal:", output.ToString());
        Assert.Empty(repository.LoadAsync().GetAwaiter().GetResult().Goals);
    }

    [Fact]
    public async Task FailedEpicAssignmentReportsOnlyConfirmedLinks()
    {
        using var fixture = new Fixture(this);
        var portfolio = new PortfolioStore(fixture.Workspace.PortfolioStorePath);
        var epic = await portfolio.AddEpicAsync("Rejected membership");
        using (var connection = new SqliteConnection($"Data Source={fixture.Workspace.PortfolioStorePath};Pooling=False"))
        {
            connection.Open();
            using var trigger = connection.CreateCommand();
            trigger.CommandText = "CREATE TRIGGER reject_summary_membership BEFORE INSERT ON goal_epic_memberships BEGIN SELECT RAISE(ABORT, 'fixture membership rejected'); END;";
            trigger.ExecuteNonQuery();
        }
        var output = Execute(fixture, ["goal", "Update receipt formatting.", "--summary", "--pipeline", "scout", "--epic", epic.Id]);
        var goal = Assert.Single((await fixture.Repository.LoadAsync()).Goals);
        Assert.Contains("Outcome: created", output);
        Assert.Contains($"Goal: {goal.Id.Value}", output);
        Assert.Contains("EPIC_ASSIGN_FAILED", output);
        Assert.Contains("fixture membership rejected", output);
        Assert.DoesNotContain($"Epic: {epic.Id}", output);
        Assert.Null(await portfolio.GetGoalMembershipAsync(goal.Id.Value));
    }

    [Fact]
    public void RendererBoundsTaskRowsAndUnicodeTitlesWithoutTruncatingWarnings()
    {
        using var fixture = new Fixture(this);
        var title = string.Concat(Enumerable.Repeat("😀e\u0301", 90)) + "\nsecond line";
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Body must not be printed", Enumerable.Range(0, 13)
            .Select(_ => new TaskSpec(TaskId.New(), title, AgentRole.Developer)).ToList());
        var warning = "Brief capability warning: " + new string('w', 700);
        kernel.RecordGoalPolicyDecision(goal.Id, warning);
        var output = CaptureConsole(() => GoalCreationSummaryView.Print(goal, "created", fixture.Workspace.PortfolioStorePath));
        var rows = output.Split(Environment.NewLine).Where(line => line.StartsWith("  ") && line.Contains("[Pending]")).ToArray();
        Assert.Equal(10, rows.Length);
        foreach (var row in rows)
        {
            var shownTitle = row[(row.IndexOf(": ", StringComparison.Ordinal) + 2)..];
            Assert.Equal(120, shownTitle.EnumerateRunes().Count());
            Assert.DoesNotContain("\uFFFD", shownTitle);
            Assert.EndsWith("…", shownTitle);
        }
        foreach (var task in goal.Tasks.Take(10)) Assert.Contains(task.Id.Value, output);
        foreach (var task in goal.Tasks.Skip(10)) Assert.DoesNotContain(task.Id.Value, output);
        Assert.Contains("3 tasks omitted.", output);
        Assert.Contains(warning, output);
        Assert.All(goal.Tasks, task => Assert.Equal(title, task.Description));
        Assert.DoesNotContain(goal.Objective, output);
    }

    private static string Execute(Fixture fixture, IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository? repository = null, bool capture = true)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        var providers = new InMemoryModelProviderRegistry([]);
        Goal? currentGoal = null;
        void Run() => CliPersistentStateRunner.ExecuteCommand(args, repository ?? fixture.Repository,
            fixture.Workspace, ref agents, providers, ref profiles, ref currentGoal);
        if (capture) return CaptureConsole(Run);
        Run();
        return string.Empty;
    }

    private static async Task<CliProcessResult> RunIsolatedApp(Fixture fixture, params string[] args)
    {
        var root = fixture.Root;
        // Pin both ends of this fixture's transport; a parent-side decoder does not
        // select the child's Console encoding. Keep the test host's console untouched.
        const string probeName = "Mcg.AgentOrchestrator.IsolatedDotnetProbe";
        var probeDirectory = FixtureProbeLocator.FindDirectory(typeof(GoalCreationSummaryTests), probeName, probeName + ".dll");
        Assert.True(probeDirectory is not null, "CLI encoding startup hook missing. Tried: " + string.Join(", ",
            FixtureProbeLocator.TriedPaths(typeof(GoalCreationSummaryTests), probeName, probeName + ".dll")));
        var encodingReceipt = Path.Combine(root, "cli-output-encoding.txt");
        var start = new ProcessStartInfo(ResolveDotnetHostPath())
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.Environment.Clear();
        foreach (var name in new[] { "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "ProgramFiles", "ProgramFiles(x86)", "DOTNET_ROOT" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.Environment[OrchestratorWorkspace.RepoRootEnvironmentVariable] = root;
        start.Environment[DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable] = Path.Combine(root, "test-dotnet");
        start.Environment["OLLAMA_BASE_URL"] = "http://127.0.0.1:1";
        start.Environment["LLAMA_CPP_BASE_URL"] = "http://127.0.0.1:1";
        start.Environment["DOTNET_STARTUP_HOOKS"] = Path.Combine(probeDirectory!, probeName + ".dll");
        start.Environment["MCG_CLI_OUTPUT_ENCODING_RECEIPT"] = encodingReceipt;
        start.ArgumentList.Add(typeof(CliPersistentStateRunner).Assembly.Location);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("App process did not start.");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("Summary CLI fixture did not produce a process-exit receipt.");
        }
        var result = new CliProcessResult(process.ExitCode, await stdout, await stderr);
        Assert.True(File.Exists(encodingReceipt), "CLI child did not acknowledge its output encoding. " + result.Stderr);
        Assert.Equal("65001", File.ReadAllText(encodingReceipt));
        // Creation may launch a fixture-owned refinement child. Its redirected log handles
        // stay open until exit; wait for their release before reading state or deleting stores.
        var logs = Directory.Exists(fixture.Workspace.LogDirectory)
            ? Directory.GetFiles(fixture.Workspace.LogDirectory, "spec-refinement-*.log") : [];
        using var childTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        foreach (var log in logs)
        {
            while (true)
            {
                try
                {
                    using var released = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.None);
                    break;
                }
                catch (IOException) when (!childTimeout.IsCancellationRequested)
                {
                    await Task.Delay(25);
                }
                catch (IOException ex)
                {
                    throw new TimeoutException($"Summary refinement child did not release its exit log: {log}", ex);
                }
            }
        }
        return result;
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("goal-summary-").FullName;
        public OrchestratorWorkspace Workspace { get; }
        public ITransactionalOrchestratorStateRepository Repository { get; }
        public Fixture(GoalCreationSummaryTests owner)
        {
            Workspace = owner.CreateRefinedWorkspace(Root);
            AgentCatalogStore.Save(Workspace.AgentCatalogPath, AgentCatalog.Default());
            Repository = CreateMigratedStateRepository(Workspace.SqliteStatePath);
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
