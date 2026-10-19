using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel safe: files, clock, transports, cancellation and handles are fixture-owned.
public sealed class SshRemoteLaneExecutorTestsToolsDigest
{
    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Submit_MatchingDigest_ChecksBeforeOccupancyAndPush(bool occupancy)
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var digest = SeedTools(fake);
        var result = await Executor(fake, true, occupancy, Manifest(digest)).SubmitAsync(
            FakeSshRemoteLaneTransport.Request, default);
        fake.Handle = Assert.IsType<SshRemoteLaneHandle>(result.Handle);
        Assert.Null(result.FailureReason);
        Assert.Equal(occupancy ? new[] { "tools-digest", "occupancy", "push", "job-copy", "trigger" }
            : new[] { "tools-digest", "push", "job-copy", "trigger" }, result.Steps!.Select(step => step.Name));
        var first = fake.Calls.First();
        Assert.Equal(new[] { SshRemoteLaneExecutor.ScpPath, "-o", "BatchMode=yes",
            "runner:C:/mcg-executor/tools-digest.json", "tools-digest.json" }, first.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(30), first.Bound);
        Assert.Equal(occupancy ? 5 : 4, fake.Calls.Count);
        Assert.Contains("expected=" + digest, result.Steps![0].StderrTail);
        Assert.Contains("observed=" + digest, result.Steps[0].StderrTail);
    }

    [Xunit.Fact]
    public async Task Submit_MismatchingDigest_RefusesBeforePush()
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var expected = SeedTools(fake);
        var observed = new string('0', 64);
        Assert.NotEqual(expected, observed);
        var result = await Executor(fake, true, true, Manifest(observed)).SubmitAsync(
            FakeSshRemoteLaneTransport.Request, default);
        Assert.Null(result.Handle);
        Assert.Equal("executor-tools-mismatch", result.FailureReason);
        var step = Assert.Single(result.Steps!);
        Assert.Equal("tools-digest", step.Name);
        Assert.Single(fake.Calls);
        Assert.DoesNotContain(fake.Calls, call => call.Arguments.Contains("push"));
        var output = File.ReadAllText(step.StdoutPath!);
        Assert.Contains("expected=" + expected, output);
        Assert.Contains("observed=" + observed, output);
    }

    [Xunit.Theory]
    [Xunit.InlineData("missing")]
    [Xunit.InlineData("malformed")]
    [Xunit.InlineData("exit1")]
    [Xunit.InlineData("timeout")]
    [Xunit.InlineData("throw")]
    [Xunit.InlineData("bound-cancel")]
    [Xunit.InlineData("no-local-tools")]
    public async Task Submit_UnverifiableDigest_RefusesBeforePush(string scenario)
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var digest = scenario == "no-local-tools" ? new string('0', 64) : SeedTools(fake);
        var manifest = scenario == "missing" ? null : scenario == "malformed" ? "{" : Manifest(digest);
        var result = await Executor(fake, true, true, manifest, scenario).SubmitAsync(
            FakeSshRemoteLaneTransport.Request, default);
        Assert.Null(result.Handle);
        Assert.Equal("executor-tools-unverified", result.FailureReason);
        var step = Assert.Single(result.Steps!);
        Assert.Equal("tools-digest", step.Name);
        Assert.Single(fake.Calls);
        Assert.DoesNotContain(fake.Calls, call => call.Arguments.Contains("push"));
        var output = File.ReadAllText(step.StdoutPath!);
        Assert.Contains("expected=", output);
        Assert.Contains("observed=", output);
        if (scenario is "timeout" or "bound-cancel") Assert.True(step.TimedOut);
    }

    [Xunit.Fact]
    public async Task Submit_StaleManifestAndMissingCopy_DoesNotReuseOldDigest()
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var digest = SeedTools(fake);
        var staging = Staging(fake);
        var path = Path.Combine(staging, ExecutorToolsDigest.ManifestFileName);
        File.WriteAllText(path, Manifest(digest));
        var result = await Executor(fake, true, false, null).SubmitAsync(FakeSshRemoteLaneTransport.Request, default);
        Assert.Null(result.Handle);
        Assert.Equal("executor-tools-unverified", result.FailureReason);
        Assert.False(File.Exists(path));
        Assert.Single(fake.Calls);
    }

    [Xunit.Fact]
    public async Task Submit_ManifestCannotBeCleared_RefusesBeforePush()
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var digest = SeedTools(fake);
        Directory.CreateDirectory(Path.Combine(Staging(fake), ExecutorToolsDigest.ManifestFileName));
        var result = await Executor(fake, true, false, Manifest(digest)).SubmitAsync(
            FakeSshRemoteLaneTransport.Request, default);
        Assert.Null(result.Handle);
        Assert.Equal("executor-tools-unverified", result.FailureReason);
        Assert.Equal("tools-digest", Assert.Single(result.Steps!).Name);
        Assert.Empty(fake.Calls);
    }

    [Xunit.Fact]
    public async Task Submit_CallerCancellation_PropagatesAfterOneCopy()
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        SeedTools(fake);
        using var stop = new CancellationTokenSource();
        var executor = Executor(fake, true, false, null, "cancel", stop);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.SubmitAsync(
            FakeSshRemoteLaneTransport.Request, stop.Token));
        Assert.Single(fake.Calls);
        Assert.DoesNotContain(fake.Calls, call => call.Arguments.Contains("push"));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Submit_CheckDisabled_MatchesBaselineCallsAndBounds(bool occupancy)
    {
        await using var mismatching = new FakeSshRemoteLaneTransport();
        await using var baseline = new FakeSshRemoteLaneTransport();
        SeedTools(mismatching);
        SeedTools(baseline);
        var actual = await Executor(mismatching, false, occupancy, Manifest(new string('0', 64)))
            .SubmitAsync(FakeSshRemoteLaneTransport.Request, default);
        var expected = await Executor(baseline, false, occupancy, null)
            .SubmitAsync(FakeSshRemoteLaneTransport.Request, default);
        mismatching.Handle = Assert.IsType<SshRemoteLaneHandle>(actual.Handle);
        baseline.Handle = Assert.IsType<SshRemoteLaneHandle>(expected.Handle);
        Assert.Null(actual.FailureReason);
        Assert.Null(expected.FailureReason);
        Assert.Equal(occupancy ? new[] { "occupancy", "push", "job-copy", "trigger" }
            : new[] { "push", "job-copy", "trigger" }, actual.Steps!.Select(step => step.Name));
        Assert.Equal(expected.Steps!.Select(step => step.Name), actual.Steps.Select(step => step.Name));
        Assert.Equal(CallSignatures(baseline.Calls), CallSignatures(mismatching.Calls));
        Assert.Equal(CallSignatures(baseline.Calls.Where(call => call.Arguments.Contains("push"))),
            CallSignatures(mismatching.Calls.Where(call => call.Arguments.Contains("push"))));
        Assert.DoesNotContain(mismatching.Calls.Concat(baseline.Calls), call =>
            call.Arguments.Any(arg => arg.Contains("tools-digest.json", StringComparison.Ordinal)));
    }

    [Xunit.Theory]
    [Xunit.InlineData("true", true, null)]
    [Xunit.InlineData(null, false, null)]
    [Xunit.InlineData("false", false, null)]
    [Xunit.InlineData("\"yes\"", false, "invalid")]
    public async Task Configuration_ToolsDigestField_ValidatesBoolean(string? field, bool enabled, string? reason)
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var path = Path.Combine(fake.Root, "executors.json");
        var option = field is null ? "" : ",\"toolsDigestCheck\":" + field;
        File.WriteAllText(path, "{\"executors\":[{\"id\":\"one\",\"transport\":\"ssh\",\"runnerAlias\":\"runner\"," +
            "\"adminAlias\":\"admin\",\"remoteRepository\":\"C:/repo/bare.git\"" + option + "}],\"lanes\":[\"lane\"]}");
        var configuration = RemoteLaneExecutorConfiguration.Load(path);
        Assert.Equal(reason, configuration.DisabledReason);
        if (reason is null)
        {
            Assert.True(configuration.Enabled);
            Assert.Equal(enabled, Assert.Single(configuration.Executors).ToolsDigestCheck);
        }
        else Assert.False(configuration.Enabled);
    }

    private static IEnumerable<(string Arguments, TimeSpan Bound)> CallSignatures(
        IEnumerable<FakeSshRemoteLaneTransport.Call> calls) =>
        calls.Select(call => (string.Join('\u0001', call.Arguments), call.Bound));

    private static string SeedTools(FakeSshRemoteLaneTransport fake)
    {
        var tools = Path.Combine(fake.Root, "scripts", "executor", "tools");
        Directory.CreateDirectory(tools);
        File.WriteAllText(Path.Combine(tools, "a.ps1"), "echo a\n");
        File.WriteAllText(Path.Combine(tools, "b.ps1"), "echo b\n");
        return ExecutorToolsDigest.Compute(tools)!;
    }

    private static string Staging(FakeSshRemoteLaneTransport fake) => SshRemoteLaneExecutor.PrepareStaging(
        Path.Combine(fake.Root, "attempt"), "one", FakeSshRemoteLaneTransport.Request.Lane,
        SshRemoteLaneExecutor.LaneKey(FakeSshRemoteLaneTransport.Request.Lane));

    private static string Manifest(string digest) => JsonSerializer.Serialize(new { digest });

    private static SshRemoteLaneExecutor Executor(FakeSshRemoteLaneTransport fake, bool enabled, bool occupancy,
        string? manifest, string scenario = "", CancellationTokenSource? stop = null) => new(
        new([new("one", 60, "ssh", "runner", "admin", "C:/repo/bare.git", "C:/mcg-executor",
            OccupancyProbe: occupancy, ToolsDigestCheck: enabled)], [FakeSshRemoteLaneTransport.Request.Lane], null),
        fake.Root, Path.Combine(fake.Root, "attempt", "result"), fake.Clock, async (args, directory, bound, token) =>
        {
            if (args[3].EndsWith("/tools-digest.json", StringComparison.Ordinal))
            {
                fake.Calls.Enqueue(new(args, directory, bound));
                Assert.False(File.Exists(Path.Combine(directory, args[^1])), "Stale digest must be cleared before scp.");
                if (scenario == "throw") throw new IOException("digest-copy-unavailable");
                if (scenario == "bound-cancel") throw new OperationCanceledException("transport bound expired");
                if (scenario == "cancel") { stop!.Cancel(); token.ThrowIfCancellationRequested(); }
                if (manifest is not null) File.WriteAllText(Path.Combine(directory, args[^1]), manifest);
                return new(scenario == "exit1" ? 1 : 0, "", TimedOut: scenario == "timeout", Stderr: "copy-note");
            }
            if (args[3].EndsWith("/runner-state.json", StringComparison.Ordinal))
            {
                fake.Calls.Enqueue(new(args, directory, bound));
                File.WriteAllText(Path.Combine(directory, args[^1]), JsonSerializer.Serialize(new
                    { utc = fake.Clock.GetUtcNow(), slots = 2, running = Array.Empty<string>(), queued = Array.Empty<string>() }));
                return new(0, "");
            }
            return await fake.Transport(args, directory, bound, token);
        }, fake.Git, TimeSpan.Zero);
}
