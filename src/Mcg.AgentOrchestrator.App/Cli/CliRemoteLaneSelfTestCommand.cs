using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

// Operator startup command, deliberately outside the acceptance-check dispatcher.
internal static class CliRemoteLaneSelfTestCommand
{
    private const string Lane = "infrastructure tests: Remote self-test";
    internal static bool IsCommand(IReadOnlyList<string> args) => args.Count > 0 &&
        args[0].Equals("remote-lane-selftest", StringComparison.OrdinalIgnoreCase);

    internal static Task<int> RunAsync(IReadOnlyList<string> args, OrchestratorWorkspace workspace) =>
        RunAsync(args, workspace, GoalAcceptanceVerifier.RunRemoteLaneTransportAsync, GitCli.Run,
            TimeProvider.System, TimeSpan.FromSeconds(1),
            $"selftest-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{Guid.NewGuid().ToString("N")[..8]}", Console.Out, Console.Error);

    internal static async Task<int> RunAsync(IReadOnlyList<string> args, OrchestratorWorkspace workspace,
        Func<string[], string, TimeSpan, CancellationToken, Task<GoalAcceptanceVerifier.CommandResult>> transport,
        Func<string, int, string[], GitCli.GitResult> git, TimeProvider clock, TimeSpan pollInterval,
        string attemptId, TextWriter output, TextWriter error)
    {
        string? id = null;
        var minutes = 20;
        for (var i = 1; i < args.Count; i++)
        {
            if (args[i] is not ("--executor" or "--timeout-minutes") || i + 1 == args.Count) return Usage();
            var flag = args[i++];
            if (flag == "--executor") id = args[i];
            else if (!int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out minutes) || minutes <= 0)
                return Usage();
        }
        var entry = RemoteLaneExecutorConfiguration.LoadExecutors(
            RemoteLaneExecutorConfiguration.ResolveStorePath(workspace.ExecutionDirectory)).FirstOrDefault(entry => entry.Id == id);
        if (entry?.Transport != "ssh") return Usage();
        IRemoteLaneHandle? handle = null;
        string? firstFailure = null;
        var lastStep = "submit";
        var passed = false;
        try
        {
            var main = git(workspace.ExecutionDirectory, 30_000, ["rev-parse", $"refs/heads/{workspace.IntegrationBranch}"]);
            if (main.ExitCode != 0 || string.IsNullOrWhiteSpace(main.Output)) { firstFailure = "resolve-main"; return 1; }
            var sha = main.Output.Trim();
            var tree = git(workspace.ExecutionDirectory, 30_000, ["rev-parse", sha + "^{tree}"]);
            if (tree.ExitCode != 0 || string.IsNullOrWhiteSpace(tree.Output)) { firstFailure = "resolve-tree"; return 1; }
            var folder = Path.Combine(AcceptancePartitionVerdictCache.ResolveHostStateRoot(workspace.ExecutionDirectory),
                ".orchestrator", "remote-lane-selftest", attemptId);
            var stagingName = SshRemoteLaneExecutor.StagingFolderName(entry.Id, SshRemoteLaneExecutor.LaneKey(Lane), 0);
            var naturalLength = Path.GetFullPath(folder).Length;
            folder += new string('x', Math.Max(0, SshRemoteLaneExecutor.RepresentativeAttemptFolderLength - naturalLength));
            var prefix = Path.Combine(folder, "result");
            var staging = Path.GetFullPath(Path.Combine(folder, stagingName));
            output.WriteLine($"staging={staging} length={staging.Length}");
            var filter = "FullyQualifiedName~RemoteLaneExecutorConfigurationTests";
            var request = new RemoteLaneRequest(entry.Id, attemptId, "remote-lane-selftest", Lane,
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                filter, GoalAcceptanceVerifier.ShortHash(filter), sha, tree.Output.Trim(), sha, "remote-lane-selftest");
            var poll = 0;
            async Task<GoalAcceptanceVerifier.CommandResult> Transport(string[] arguments, string directory, TimeSpan bound, CancellationToken token)
            {
                var step = arguments.Any(arg => arg.Contains("/queue/", StringComparison.Ordinal)) ? "job-copy"
                    : arguments.Contains("schtasks") ? "trigger"
                    : arguments.Any(arg => arg.EndsWith("/status.json", StringComparison.Ordinal)) ? $"poll-{++poll}" : "fetch";
                lastStep = step;
                var result = await transport(arguments, directory, bound, token).ConfigureAwait(false);
                PrintStep(step, result.ExitCode, result.TimedOut, result.Stderr ?? "");
                return result;
            }
            GitCli.GitResult Git(string directory, int bound, string[] arguments)
            {
                lastStep = "push";
                var result = git(directory, bound, arguments);
                PrintStep("push", result.ExitCode, result.DrainTimedOut || result.ExitCode == -1, result.Error);
                return result;
            }
            void PrintStep(string step, int exit, bool timedOut, string stderr)
            {
                if (exit != 0 || timedOut) Interlocked.CompareExchange(ref firstFailure, step, null);
                var tail = stderr.Length > 300 ? stderr[^300..] : stderr;
                output.WriteLine($"step={step} exit={exit} timed_out={timedOut.ToString().ToLowerInvariant()} stderr={tail.Replace('\r', ' ').Replace('\n', ' ')}");
            }
            var executor = new SshRemoteLaneExecutor(new([entry], [Lane], null), workspace.ExecutionDirectory,
                prefix, clock, Transport, Git, pollInterval);
            var started = clock.GetUtcNow();
            var submission = await executor.SubmitAsync(request, CancellationToken.None).ConfigureAwait(false);
            handle = submission.Handle;
            if (handle is null)
            {
                firstFailure ??= submission.FailureReason switch
                { "push-failed" => "push", "job-copy-failed" => "job-copy", "trigger-failed" => "trigger", _ => "submit" };
                output.WriteLine($"result={submission.FailureReason}");
                return 1;
            }
            while (true)
            {
                if (handle.TryGetResult() is { } result)
                {
                    output.WriteLine($"result={JsonSerializer.Serialize(result)}");
                    var mismatch = RemoteLaneCoordinator.BindingMismatch(request, result);
                    output.WriteLine($"binding={mismatch?.ToString() ?? "match"}");
                    var trx = result.TestResultPaths.Count > 0 && result.TestResultPaths.All(File.Exists);
                    output.WriteLine($"trx_exists={trx.ToString().ToLowerInvariant()}");
                    passed = mismatch is null && result.ExitCode == 0 && trx;
                    if (!passed) firstFailure ??= mismatch is not null ? "binding" : result.ExitCode != 0 ? "remote-exit" : "trx";
                    return passed ? 0 : 1;
                }
                if (clock.GetUtcNow() - started >= TimeSpan.FromMinutes(minutes))
                { firstFailure ??= "wait-timeout"; return 1; }
                if (pollInterval > TimeSpan.Zero) await Task.Delay(pollInterval).ConfigureAwait(false);
                else await Task.Yield();
            }
        }
        catch (Exception ex)
        {
            firstFailure ??= ex.Message.StartsWith("result-fetch-failed", StringComparison.Ordinal) ? "fetch" : lastStep;
            error.WriteLine($"result={ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            if (handle is not null)
            {
                try { handle.Abandon(); if (handle is SshRemoteLaneHandle ssh) await ssh.PollLoop.ConfigureAwait(false); }
                catch (Exception) { }
            }
            output.WriteLine(passed ? "SELFTEST result=passed" : $"SELFTEST result=failed step={firstFailure ?? lastStep}");
        }
        int Usage() { error.WriteLine(CliCommandHelp.RemoteLaneSelfTestUsage); return 2; }
    }
}
