using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: a private real repository, GUID pipe and root-scoped mutex per case.
public sealed class GoalWorktreeCreationSweepSerializationTests : GoalWorktreeTestBase
{
    private const string ProbeName = "Mcg.AgentOrchestrator.WorktreeSerializationProbe";
    private const string LastCheckoutEntry = "tools/zz-last-checkout.txt";

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task CheckoutInProgress_ConcurrentSweep_KeepsCompleteWorktree(bool separateProcess)
    {
        var repo = CreateSeededRepository();
        var pipeName = "mcg-worktree-checkout-" + Guid.NewGuid().ToString("n");
        using var gate = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var hangGuard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        Task<(string Path, int ProcessId)>? creation = null;
        var checkoutReleased = false;
        try
        {
            var probe = ResolveProbe();
            var dotnet = ResolveDotnet();
            Directory.CreateDirectory(Path.Combine(repo, "src"));
            Directory.CreateDirectory(Path.Combine(repo, "tools"));
            File.WriteAllText(Path.Combine(repo, "src", "first.txt"), "source survives checkout");
            File.WriteAllText(Path.Combine(repo, LastCheckoutEntry), "last checkout entry\n");
            File.WriteAllText(Path.Combine(repo, ".gitattributes"), LastCheckoutEntry + " filter=sweep-gate\n");
            RunGit(repo, "add", "src", "tools", ".gitattributes");
            RunGit(repo, "commit", "-m", "Seed event-gated checkout");
            Assert.Equal(LastCheckoutEntry, RunGitOutput(repo, "ls-files").Split('\n').Last().TrimEnd('\r'));
            RunGit(repo, "config", "filter.sweep-gate.smudge",
                $"{ShellQuote(dotnet)} {ShellQuote(probe)} smudge {pipeName}");
            RunGit(repo, "config", "filter.sweep-gate.clean", $"{ShellQuote(dotnet)} {ShellQuote(probe)} clean");
            RunGit(repo, "config", "filter.sweep-gate.required", "true");

            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Creation versus sweep", [new(TaskId.New(), "Work", AgentRole.Developer)]);
            kernel.CancelGoal(goal.Id, "Disable live-goal protection to isolate serialization.");
            Assert.True(goal.IsTerminal);
            creation = separateProcess
                ? CreateInChild(dotnet, probe, repo, goal.Id)
                : Task.Run(() => (GoalWorktrees.Ensure(repo, goal.Id), Environment.ProcessId));

            await gate.WaitForConnectionAsync(hangGuard.Token);
            using var readiness = new StreamReader(gate, leaveOpen: true);
            Assert.Equal("checkout-paused", await readiness.ReadLineAsync(hangGuard.Token));
            var path = GoalWorktrees.WorktreePath(repo, goal.Id);
            Assert.True(File.Exists(Path.Combine(path, ".git")));
            Assert.True(File.Exists(Path.Combine(path, "src", "first.txt")));
            // The smudge handshake proves checkout has not written this entry yet.
            Assert.False(File.Exists(Path.Combine(path, LastCheckoutEntry)));
            var fixture = new OrphanSweepTestFixture(repo);
            var listingCalls = 0;
            var result = fixture.Sweep(kernel, fixture.Hooks with
            {
                // Deliberately empty registrations + future clock + terminal goal:
                // only serialization can prevent deletion of this partial checkout.
                RunWorktreeList = _ => { listingCalls++; return new(0, "", ""); }
            });

            Assert.Equal(0, result.RemovedCount);
            Assert.Empty(result.LeftoverPaths);
            Assert.Equal(0, listingCalls);
            Assert.Empty(fixture.Deletions);
            Assert.Single(fixture.Outcomes);
            fixture.AssertOutcome(path, "lock-unavailable", goal.Id);
            Assert.Empty(fixture.Warnings);

            // Write directly: disposing a buffered writer after the filter exits
            // would flush the pipe again after its reader has closed.
            await gate.WriteAsync("release\n"u8.ToArray(), hangGuard.Token);
            checkoutReleased = true;
            var created = await creation.WaitAsync(hangGuard.Token);
            Assert.Equal(path, created.Path);
            Assert.Equal(separateProcess, created.ProcessId != Environment.ProcessId);
            Assert.True(File.Exists(Path.Combine(path, ".git")));
            Assert.True(Directory.Exists(Path.Combine(path, "src")));
            Assert.Equal("source survives checkout", File.ReadAllText(Path.Combine(path, "src", "first.txt")));
            Assert.Equal("last checkout entry\n", File.ReadAllText(Path.Combine(path, LastCheckoutEntry)).Replace("\r\n", "\n"));
            var listing = RunGitOutput(repo, "worktree", "list", "--porcelain");
            Assert.Contains("worktree " + path.Replace('\\', '/'), listing.Replace('\\', '/'));
            Assert.DoesNotContain("prunable", listing, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(path, GoalWorktrees.TryResolve(repo, goal.Id));

            // A following pass acquires the released lock and observes real git state.
            fixture.Outcomes.Clear();
            Assert.Equal(0, fixture.Sweep(kernel).RemovedCount);
            Assert.Single(fixture.Outcomes);
            fixture.AssertOutcome(path, "registered", goal.Id);
        }
        finally
        {
            if (!checkoutReleased && gate.IsConnected)
            {
                try { await gate.WriteAsync("release\n"u8.ToArray()); }
                catch (IOException) { /* The filter already exited on the failure path. */ }
            }
            try
            {
                if (creation is not null) await creation.WaitAsync(TimeSpan.FromSeconds(120));
            }
            finally { DeleteDirectory(repo); }
        }
    }

    private static async Task<(string Path, int ProcessId)> CreateInChild(
        string dotnet, string probe, string repo, GoalId goalId)
    {
        var start = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = repo,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(probe);
        start.ArgumentList.Add("create");
        start.ArgumentList.Add(repo);
        start.ArgumentList.Add(goalId.Value);
        // Explicit allow-list: provider/handoff/gate switches cannot alter this child.
        start.Environment.Clear();
        foreach (var name in new[] { "PATH", "SystemRoot", "WINDIR", "COMSPEC", "PATHEXT",
                     "TEMP", "TMP", "HOME", "USERPROFILE", "LOCALAPPDATA", "APPDATA" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(dotnet);
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(repo, ".git", "test-global-config");
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        var ownedProcessId = 0;
        var result = await CliChildProcessRunner.RunAsync(start, TimeSpan.FromSeconds(120),
            started: process => ownedProcessId = process.Id);
        Assert.True(result.ExitCode == 0, $"Creation child exit={result.ExitCode}: {result.StandardError}");
        using var receipt = JsonDocument.Parse(result.StandardOutput);
        var processId = receipt.RootElement.GetProperty("ProcessId").GetInt32();
        Assert.Equal(ownedProcessId, processId);
        return (receipt.RootElement.GetProperty("Path").GetString()!, processId);
    }

    private static string ResolveProbe()
    {
        var directory = FixtureProbeLocator.FindDirectory(typeof(GoalWorktreeCreationSweepSerializationTests),
            ProbeName, ProbeName + ".dll", ProbeName + ".runtimeconfig.json");
        if (directory is null)
        {
            var configuration = typeof(GoalWorktreeCreationSweepSerializationTests).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
                .Cast<System.Reflection.AssemblyConfigurationAttribute>().Single().Configuration;
            directory = Path.Combine(FindCurrentSourceRoot(), "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests",
                "Fixtures", "WorktreeSerializationProbe", "bin", configuration, "net10.0");
        }
        var assembly = Path.Combine(directory, ProbeName + ".dll");
        Assert.True(File.Exists(assembly), "Build the WorktreeSerializationProbe project: " + assembly);
        Assert.True(File.Exists(Path.Combine(directory, ProbeName + ".runtimeconfig.json")));
        return assembly;
    }

    private static string ResolveDotnet()
    {
        var host = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..",
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        Assert.True(File.Exists(host), "No dotnet host beside the executing runtime: " + host);
        return host;
    }

    private static string ShellQuote(string value) => "'" + value.Replace('\\', '/').Replace("'", "'\\''") + "'";
}
