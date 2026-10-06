using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class ProcessTreeGuiSuppressionTestsChildConsoleLaunchPolicy
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void SelectPreservesDefaultAndOnlyInheritsAnAttachedWindowlessConsole(bool hasWindow, bool attached)
    {
        var calls = 0;
        bool IsAttached() { calls++; return attached; }
        var off = ChildConsoleLaunchPolicy.Select(ChildConsoleExperiment.Off, hasWindow, IsAttached);
        Assert.Equal(hasWindow ? 0u : ChildConsoleLaunchPolicy.CreateNoWindow, off.ChildCreationFlags);
        Assert.Equal(!hasWindow, off.ChildCreateNoWindow);
        Assert.Equal(0, calls);

        var inherit = ChildConsoleLaunchPolicy.Select(ChildConsoleExperiment.InheritWindowlessConsole, hasWindow, IsAttached);
        Assert.Equal(hasWindow || attached ? 0u : ChildConsoleLaunchPolicy.CreateNoWindow, inherit.ChildCreationFlags);
        Assert.Equal(!hasWindow && !attached, inherit.ChildCreateNoWindow);
        Assert.Equal(hasWindow ? 0 : 1, calls);
    }

    [Fact]
    public async Task FreshMeasurementHostReportsExperimentOff()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var report = await ConsoleHostExperimentHarness.Run(1, "both", startupOnly: true);
        Assert.Equal("Off", report.RootElement.GetProperty("switchAtStartup").GetString());
    }
}
