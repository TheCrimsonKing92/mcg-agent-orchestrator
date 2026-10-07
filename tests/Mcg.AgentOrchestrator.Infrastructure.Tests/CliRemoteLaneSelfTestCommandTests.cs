using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CliRemoteLaneSelfTestCommandTests
{
    private const string Attempt = "selftest-fixed";
    private const string Sha = "123456789abcdef";
    private const string Lane = "infrastructure tests: Remote self-test";
    private const string Filter = "FullyQualifiedName~RemoteLaneExecutorConfigurationTests";

    [Xunit.Fact]
    public async Task CompletedMatchingJobPrintsEachCallLongPathAndPass()
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        Configure(fake.Root);
        var status = Status();
        await fake.Polls.Writer.WriteAsync(new(status));
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter();
        var result = await Run(fake, output, error);
        Assert.Equal(0, result);
        Assert.Equal("", error.ToString());
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var stagingLine = Assert.Single(lines.Where(line => line.StartsWith("staging=")));
        var length = int.Parse(stagingLine[(stagingLine.LastIndexOf("length=", StringComparison.Ordinal) + 7)..], CultureInfo.InvariantCulture);
        Assert.True(length >= 156, stagingLine);
        var staging = stagingLine[8..stagingLine.LastIndexOf(" length=", StringComparison.Ordinal)];
        Assert.Equal(length, staging.Length);
        Assert.StartsWith(Path.Combine(fake.Root, ".orchestrator", "remote-lane-selftest"), staging);
        Assert.Equal(new[] { "push", "job-copy", "trigger", "poll-1", "fetch", "fetch" },
            lines.Where(line => line.StartsWith("step=")).Select(line => line.Split(' ')[0][5..]));
        Assert.Equal(fake.Calls.Count, lines.Count(line => line.StartsWith("step=")));
        Assert.All(lines.Where(line => line.StartsWith("step=")), line => Assert.Contains("exit=0 timed_out=false stderr=", line));
        Assert.Contains("binding=match", lines);
        Assert.Contains("trx_exists=true", lines);
        Assert.Equal("SELFTEST result=passed", lines[^1]);
        var job = Assert.Single(Directory.GetFiles(staging, "*.json"));
        using var json = JsonDocument.Parse(File.ReadAllText(job));
        Assert.Equal(Lane, json.RootElement.GetProperty("lane").GetString());
        Assert.Equal(Filter, json.RootElement.GetProperty("filter").GetString());
        Assert.Equal(Sha, json.RootElement.GetProperty("sha").GetString());
        Assert.Equal(Sha, json.RootElement.GetProperty("mainSha").GetString());
        Assert.Equal("remote-lane-selftest", json.RootElement.GetProperty("manifestIdentity").GetString());
        Assert.Equal(Attempt, json.RootElement.GetProperty("attemptId").GetString());
        Assert.False(File.Exists(RemoteExecutorHealthLedger.ResolveStorePath(fake.Root)));
    }

    [Xunit.Fact]
    public async Task FailedJobCopyReportsFirstFailingStepAndStopsBeforeTrigger()
    {
        await using var fake = new FakeSshRemoteLaneTransport { FailStep = 1, Noise = new string('e', 400) + "\r\nend" };
        Configure(fake.Root);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, await Run(fake, output, error));
        Assert.EndsWith("SELFTEST result=failed step=job-copy" + Environment.NewLine, output.ToString());
        Assert.Equal(2, fake.Calls.Count);
        var steps = output.ToString().Split(Environment.NewLine).Where(line => line.StartsWith("step=")).ToArray();
        Assert.Equal(2, steps.Length);
        Assert.Contains("step=job-copy exit=1 timed_out=false", steps[1]);
        Assert.Equal(300, steps[1][(steps[1].IndexOf("stderr=", StringComparison.Ordinal) + 7)..].Length);
    }

    [Xunit.Theory]
    [Xunit.InlineData("unknown")]
    [Xunit.InlineData("legacy")]
    public async Task UnknownOrNonSshExecutorReturnsUsageBeforeGitOrTransport(string id)
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        Configure(fake.Root);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var calls = 0;
        Assert.Equal(2, await CliRemoteLaneSelfTestCommand.RunAsync(["remote-lane-selftest", "--executor", id],
            OrchestratorWorkspace.ForDirectory(fake.Root),
            (_, _, _, _) => { calls++; throw new InvalidOperationException("transport must not run"); },
            (_, _, _) => { calls++; throw new InvalidOperationException("git must not run"); },
            fake.Clock, TimeSpan.Zero, Attempt, output, error));
        Assert.Equal(0, calls);
        Assert.Equal("", output.ToString());
        Assert.Equal(CliCommandHelp.RemoteLaneSelfTestUsage + Environment.NewLine, error.ToString());
    }

    [Xunit.Fact]
    public async Task MismatchedBindingFailsEvenWithSuccessfulTransportAndTrx()
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        Configure(fake.Root);
        var status = Status();
        status["treeSha"] = "wrong-tree";
        await fake.Polls.Writer.WriteAsync(new(status));
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, await Run(fake, output, error));
        Assert.Contains("binding=BindingMismatchTree", output.ToString());
        Assert.EndsWith("SELFTEST result=failed step=binding" + Environment.NewLine, output.ToString());
    }

    private static Task<int> Run(FakeSshRemoteLaneTransport fake, TextWriter output, TextWriter error) =>
        CliRemoteLaneSelfTestCommand.RunAsync(["remote-lane-selftest", "--executor", "one"],
            OrchestratorWorkspace.ForDirectory(fake.Root), fake.Transport,
            (directory, bound, args) => args[0] == "rev-parse"
                ? new GitCli.GitResult(0, args[1] == "refs/heads/main" ? Sha : "tree", "")
                : fake.Git(directory, bound, args),
            fake.Clock, TimeSpan.Zero, Attempt, output, error);
    private static Dictionary<string, object?> Status() => new()
    {
        ["state"] = "completed", ["attemptId"] = Attempt, ["executorId"] = "one", ["lane"] = Lane,
        ["filterHash"] = GoalAcceptanceVerifier.ShortHash(Filter), ["commitSha"] = Sha, ["treeSha"] = "tree",
        ["mainSha"] = Sha, ["manifestIdentity"] = "remote-lane-selftest", ["exitCode"] = 0,
        ["trx"] = new[] { "one.trx", "two.trx" }
    };
    private static void Configure(string root)
    {
        var path = RemoteLaneExecutorConfiguration.ResolveStorePath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"executors":[{"id":"one","transport":"ssh","runnerAlias":"runner","adminAlias":"admin","remoteRepository":"C:/repo/bare.git"},{"id":"legacy"}],"lanes":[]}""");
    }
}
