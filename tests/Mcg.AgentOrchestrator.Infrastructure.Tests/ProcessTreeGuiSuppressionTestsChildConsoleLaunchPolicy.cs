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
        Assert.Equal("InheritWindowlessConsole", report.RootElement.GetProperty("switchAtStartup").GetString());
    }

    [Fact]
    public void DefaultModeInheritsWindowlessConsole()
    {
        Assert.Equal(ChildConsoleExperiment.InheritWindowlessConsole, ChildConsoleLaunchPolicy.DefaultExperiment);
        Assert.Equal(ChildConsoleExperiment.InheritWindowlessConsole, ChildConsoleLaunchPolicy.ParseOffSwitch(null));
    }

    [Theory]
    [InlineData("off", true)]
    [InlineData("OFF", true)]
    [InlineData("Off", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("on", false)]
    [InlineData("1", false)]
    [InlineData(" off ", false)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    public void OffSwitchOnlyAcceptsExactCaseInsensitiveOff(string? value, bool off)
        => Assert.Equal(off ? ChildConsoleExperiment.Off : ChildConsoleExperiment.InheritWindowlessConsole,
            ChildConsoleLaunchPolicy.ParseOffSwitch(value));

    [Fact]
    public async Task FreshMeasurementHostWithOffSwitchReportsOff()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var report = await ConsoleHostExperimentHarness.Run(1, "both", startupOnly: true,
            environmentOverrides: new Dictionary<string, string> { [ChildConsoleLaunchPolicy.OffSwitchVariable] = "off" });
        Assert.Equal("Off", report.RootElement.GetProperty("switchAtStartup").GetString());
    }
}
