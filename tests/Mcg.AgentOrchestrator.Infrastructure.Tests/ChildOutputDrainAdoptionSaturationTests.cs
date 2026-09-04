using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Serialized: every fact deliberately occupies the thread pool while a production child-output path runs.
[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class ChildOutputDrainAdoptionSaturationTests
{
    [Xunit.Fact(DisplayName = "GoalWorktrees_direct_git_returns_complete_output_with_saturated_thread_pool")]
    public void GoalWorktreesDirectGitReturnsCompleteOutputWithSaturatedThreadPool()
    {
        PipeDrainThreadPoolSaturationTests.RunWithSaturatedThreadPool(() =>
        {
            var result = GoalWorktrees.RunGitDirect(Environment.CurrentDirectory, "--version");

            Assert.Equal(0, result.ExitCode);
            Assert.False(result.DrainTimedOut, result.Error);
            Assert.Contains("git version", result.Output, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Xunit.Fact(DisplayName = "AcceptanceGitTextResolver_returns_complete_output_with_saturated_thread_pool")]
    public void AcceptanceGitTextResolverReturnsCompleteOutputWithSaturatedThreadPool()
    {
        PipeDrainThreadPoolSaturationTests.RunWithSaturatedThreadPool(() =>
        {
            var result = AcceptanceGitTextResolver.Resolve(Environment.CurrentDirectory, "--version");

            Assert.Contains("git version", Assert.IsType<string>(result), StringComparison.OrdinalIgnoreCase);
        });
    }

    [Xunit.Fact(DisplayName = "LockAttribution_handle_probe_returns_parsed_output_with_saturated_thread_pool")]
    public void LockAttributionHandleProbeReturnsParsedOutputWithSaturatedThreadPool()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcg-lock-{Guid.NewGuid():N}.dll");
        LockAttribution.HandleExecutableForTests = "cmd.exe";
        LockAttribution.HandleProbeTimeoutForTests = TimeSpan.FromSeconds(5);
        LockAttribution.DisableRestartManagerForTests = true;
        LockAttribution.ConfigureHandleProbeForTests = (startInfo, _) =>
        {
            startInfo.ArgumentList.Clear();
            AddEchoArguments(startInfo.ArgumentList, $"test-host pid: {Environment.ProcessId}");
        };
        try
        {
            PipeDrainThreadPoolSaturationTests.RunWithSaturatedThreadPool(() =>
            {
                var result = LockAttribution.Attribute(path);

                Assert.Equal("handle64", result.Source);
                Assert.Contains(result.Holders, holder => holder.ProcessId == Environment.ProcessId);
            });
        }
        finally
        {
            LockAttribution.HandleExecutableForTests = null;
            LockAttribution.HandleProbeTimeoutForTests = null;
            LockAttribution.ConfigureHandleProbeForTests = null;
            LockAttribution.DisableRestartManagerForTests = false;
        }
    }

    [Xunit.Fact(DisplayName = "LocalProcessVerifier_returns_complete_output_with_saturated_thread_pool")]
    public void LocalProcessVerifierReturnsCompleteOutputWithSaturatedThreadPool()
    {
        const string marker = "local-verifier-complete-output";
        PipeDrainThreadPoolSaturationTests.RunWithSaturatedThreadPool(() =>
        {
            var result = LocalProcessVerifier.RunCommandAsync(
                    "cmd.exe",
                    EchoArguments(marker),
                    Environment.CurrentDirectory,
                    TimeSpan.FromSeconds(5),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            Assert.Equal(0, result.ExitCode);
            Assert.False(result.TimedOut, result.Stderr);
            Assert.Contains(marker, result.Stdout, StringComparison.Ordinal);
        });
    }

    [Xunit.Fact(DisplayName = "Cli_stable_slot_process_returns_complete_output_with_saturated_thread_pool")]
    public void CliStableSlotProcessReturnsCompleteOutputWithSaturatedThreadPool()
    {
        const string marker = "cli-complete-output";
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            PipeDrainThreadPoolSaturationTests.RunWithSaturatedThreadPool(() =>
            {
                var exitCode = CliCommandHandlers.RunStableSlotProcess(
                    "cmd.exe",
                    EchoArguments(marker),
                    Environment.CurrentDirectory,
                    configureDotnetEnvironment: false);

                Assert.Equal(0, exitCode);
            });

            Assert.Contains(marker, output.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorSelfRelaunch_process_returns_complete_output_with_saturated_thread_pool")]
    public void ConductorSelfRelaunchProcessReturnsCompleteOutputWithSaturatedThreadPool()
    {
        const string marker = "self-relaunch-complete-output";
        PipeDrainThreadPoolSaturationTests.RunWithSaturatedThreadPool(() =>
        {
            var result = ConductorSelfRelaunch.RunProcessForTests(
                "cmd.exe",
                EchoArguments(marker),
                Environment.CurrentDirectory,
                TimeSpan.FromSeconds(5));

            Assert.Equal(0, result.ExitCode);
            Assert.False(result.TimedOut, result.Stderr);
            Assert.Contains(marker, result.Stdout, StringComparison.Ordinal);
        });
    }

    [Xunit.Fact(DisplayName = "Hermes_lifecycle_returns_complete_stderr_with_saturated_thread_pool")]
    public void HermesLifecycleReturnsCompleteStderrWithSaturatedThreadPool()
    {
        const string marker = "hermes-stderr-complete-output";
        PipeDrainThreadPoolSaturationTests.RunWithSaturatedThreadPool(() =>
        {
            var result = RunHermesLifecycle(marker);

            Assert.Contains(marker, result.Progress, StringComparison.Ordinal);
            Assert.True(result.Receipt.Completed, result.Receipt.Failure);
        });
    }

    [Xunit.Fact(DisplayName = "Hermes_version_preflight_reads_complete_output_with_saturated_thread_pool")]
    public void HermesVersionPreflightReadsCompleteOutputWithSaturatedThreadPool()
    {
        PipeDrainThreadPoolSaturationTests.RunWithSaturatedThreadPool(() =>
        {
            var result = RunHermesLifecycle(string.Empty);

            Assert.True(result.Receipt.Completed, result.Receipt.Failure);
        });
    }

    private static (HermesAcpTerminalReceipt Receipt, string Progress) RunHermesLifecycle(string stderr)
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-hermes-drain-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var sandbox = Path.Combine(root, "sandbox");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(sandbox);
        var promptPath = Path.Combine(workspace, "brief.md");
        File.WriteAllText(promptPath, "drain test prompt");
        var request = new HermesAcpRequest(
            promptPath,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("drain test prompt"))).ToLowerInvariant(),
            workspace,
            sandbox,
            "gpt-test",
            "OpenAI",
            AgentRole.Developer);
        var version = new FakeHermesProcess(
            new PipeDrainThreadPoolSaturationTests.SynchronousOnlyTextReader(
                $"Hermes {HermesAcpAdapter.PinnedRelease} {HermesAcpAdapter.PinnedCommit}"),
            TextReader.Null);
        var acp = new FakeHermesProcess(new StringReader(SuccessProtocol()), new StringReader(stderr));
        using var progress = new StringWriter();
        try
        {
            var task = new HermesAcpLifecycle(launcher: new FakeHermesProcessLauncher(version, acp)).RunAsync(
                request,
                Path.Combine(sandbox, "terminal-receipt.json"),
                progress);
            Assert.True(task.Wait(TimeSpan.FromSeconds(3)), "Hermes lifecycle did not complete while the thread pool was saturated.");
            return (task.GetAwaiter().GetResult(), progress.ToString());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static string SuccessProtocol() => string.Join(Environment.NewLine,
    [
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, result = new { protocolVersion = 1 } }),
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 2,
            result = new { sessionId = "session-drain", models = new { currentModelId = "OpenAI:gpt-test" } }
        }),
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method = "session/update",
            @params = new
            {
                sessionId = "session-drain",
                update = new
                {
                    sessionUpdate = "agent_message_chunk",
                    content = new { type = "text", text = SuccessfulWorkerResult }
                }
            }
        }),
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 3,
            result = new { stopReason = "end_turn", usage = new { inputTokens = 1, outputTokens = 1, totalTokens = 2 } }
        })
    ]);

    private const string SuccessfulWorkerResult =
        """
        WORKER_RESULT:
        files: none
        commands: none
        tests: deferred - saturation adoption fixture
        commit: none
        blockers: none
        model_fit: fake/test - adequate - test - deterministic
        skills: none
        confidence: high
        END_WORKER_RESULT
        """;

    private static string[] EchoArguments(string marker) => ["/d", "/c", "echo", marker];

    private static void AddEchoArguments(System.Collections.ObjectModel.Collection<string> arguments, string marker)
    {
        foreach (var argument in EchoArguments(marker))
        {
            arguments.Add(argument);
        }
    }

    private sealed class FakeHermesProcessLauncher(params FakeHermesProcess[] processes) : IHermesAcpProcessLauncher
    {
        private readonly Queue<FakeHermesProcess> _processes = new(processes);

        public IHermesAcpProcess Start(ProcessStartInfo startInfo) => _processes.Dequeue();
    }

    private sealed class FakeHermesProcess(TextReader output, TextReader error) : IHermesAcpProcess
    {
        private readonly StringWriter _input = new();

        public TextWriter StandardInput => _input;
        public TextReader StandardOutput => output;
        public TextReader StandardError => error;
        public int ExitCode => 0;
        public bool JobExitConfirmed => true;
        public bool SurvivorInventoryEmpty => true;
        public void CompleteInput() { }
        public void Kill() { }
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
            _input.Dispose();
            output.Dispose();
            error.Dispose();
        }
    }
}
