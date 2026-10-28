using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.GoalAcceptanceVerifier)]
public sealed class GoalAcceptanceVerifierCrosswalkBoundaryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("sub")]
    public async Task ContainedProject_UsesResolvedDirectory(string? project)
    {
        using var fixture = new CrosswalkAcceptanceFixture(project: project);
        Directory.CreateDirectory(Path.Combine(fixture.Root, "sub"));
        var check = await fixture.RunAsync();
        Assert.True(check.Passed, check.OutputTail);
        Assert.Equal(2, fixture.Calls.Count);
        Assert.All(fixture.Calls, call => Assert.Equal(project == "sub" ? Path.Combine(fixture.Root, "sub") : fixture.Root, call.Root));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("../candidate-sibling")]
    [InlineData("C:\\outside")]
    [InlineData("C:relative")]
    [InlineData("/outside")]
    [InlineData("missing")]
    public async Task EscapedOrUnresolvedRoot_FailsBeforeList(string project)
    {
        using var fixture = new CrosswalkAcceptanceFixture(project: project);
        var check = await fixture.RunAsync();
        Assert.False(check.Passed);
        Assert.Equal(CrosswalkDiagnosticCategory.InvalidProject, Assert.Single(check.CrosswalkDiagnostics!).Category);
        Assert.Empty(fixture.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectoryLinks_EnforcePhysicalContainment(bool contained)
    {
        using var fixture = new CrosswalkAcceptanceFixture(project: "link/sub");
        var target = contained ? Path.Combine(fixture.Root, "target") : fixture.Root + "-outside";
        Directory.CreateDirectory(Path.Combine(target, "sub"));
        var link = Path.Combine(fixture.Root, "link");
        try
        {
            await CreateLinkAsync(fixture.Root, link, target);
            Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint));
            var check = await fixture.RunAsync();
            Assert.Equal(contained, check.Passed);
            if (contained) Assert.All(fixture.Calls, call => Assert.Equal(Path.Combine(target, "sub"), call.Root));
            else
            {
                Assert.Empty(fixture.Calls);
                Assert.Equal(CrosswalkDiagnosticCategory.InvalidProject, Assert.Single(check.CrosswalkDiagnostics!).Category);
            }
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            if (!contained && Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinkTargetAncestorEscapeOrDanglingLink_FailsBeforeList(bool dangling)
    {
        using var fixture = new CrosswalkAcceptanceFixture(project: "link");
        var target = dangling ? Path.Combine(fixture.Root, "target") : fixture.Root + "-outside";
        Directory.CreateDirectory(Path.Combine(target, "sub"));
        var bridge = Path.Combine(fixture.Root, "bridge");
        var link = Path.Combine(fixture.Root, "link");
        try
        {
            await CreateLinkAsync(fixture.Root, bridge, target);
            await CreateLinkAsync(fixture.Root, link, Path.Combine(bridge, "sub"));
            if (dangling) Directory.Delete(Path.Combine(target, "sub"));
            var check = await fixture.RunAsync();
            Assert.False(check.Passed);
            Assert.Empty(fixture.Calls);
            Assert.Equal(CrosswalkDiagnosticCategory.InvalidProject, Assert.Single(check.CrosswalkDiagnostics!).Category);
        }
        finally
        {
            if (new DirectoryInfo(link).LinkTarget is not null) Directory.Delete(link);
            if (new DirectoryInfo(bridge).LinkTarget is not null) Directory.Delete(bridge);
            if (!dangling && Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }

    private static async Task CreateLinkAsync(string root, string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        var created = await WorkerProcessRunner.RunBufferedAsync(new(
            $"New-Item -ItemType Junction -Path '{link.Replace("'", "''")}' -Target '{target.Replace("'", "''")}' | Out-Null",
            root, TimeSpan.FromSeconds(30)), CancellationToken.None);
        Assert.Equal(0, created.ExitCode);
    }

    [Fact]
    public async Task SharedDeadline_RejectsLateDiscovery_WithoutLaunchingRun()
    {
        var clock = new CrosswalkClock();
        using var fixture = new CrosswalkAcceptanceFixture(clock: clock);
        fixture.Handler = (_, _) =>
        {
            clock.Advance(TimeSpan.FromMinutes(2));
            return Task.FromResult(fixture.Result(fixture.Discovery));
        };
        var check = await fixture.RunAsync();
        Assert.False(check.Passed);
        Assert.Equal(CrosswalkDiagnosticCategory.Timeout, check.CrosswalkDiagnostics![0].Category);
        Assert.Single(fixture.Calls);
    }

    [Fact]
    public async Task ListAndRun_ReceiveOnlyRemainingBudget_AndLateRunCannotPass()
    {
        var clock = new CrosswalkClock();
        using var fixture = new CrosswalkAcceptanceFixture(clock: clock);
        fixture.Handler = (command, _) =>
        {
            var list = command.Contains(" list ", StringComparison.Ordinal);
            clock.Advance(TimeSpan.FromSeconds(list ? 20 : 50));
            return Task.FromResult(fixture.Result(list ? fixture.Discovery : fixture.Output));
        };
        var check = await fixture.RunAsync();
        Assert.False(check.Passed);
        Assert.Equal(TimeSpan.FromMinutes(1), fixture.Calls[0].Budget);
        Assert.Equal(TimeSpan.FromSeconds(40), fixture.Calls[1].Budget);
        Assert.Contains(check.CrosswalkDiagnostics!, d => d.Category == CrosswalkDiagnosticCategory.Timeout);
        Assert.Single(check.NormalizedTestResultPaths!);
    }

    [Fact]
    public async Task OwnerCancellation_DrainsStartedInvocation_AndRejectsIgnoringRunnerSuccess()
    {
        using var fixture = new CrosswalkAcceptanceFixture();
        await using var owner = fixture.Owner();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler = async (_, token) =>
        {
            entered.SetResult();
            await release.Task;
            Assert.True(token.IsCancellationRequested);
            return fixture.Result(fixture.Discovery);
        };
        var pending = fixture.RunOwnedAsync(owner);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30)); // hang-only: discovery runner did not enter
        owner.Cancel();
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        await owner.DrainAsync();
        Assert.Single(fixture.Calls);
    }

    private sealed class CrosswalkClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }
}
