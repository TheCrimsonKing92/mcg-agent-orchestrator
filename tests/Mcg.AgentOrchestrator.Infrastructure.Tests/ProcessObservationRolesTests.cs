using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ProcessObservationRolesTests
{
    [Fact]
    public void CommandCandidatesRetainUnknownRecordsAndDoNotMutateContainment()
    {
        int[] owned = [10, 20, 30, 40, 50];
        var consolePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "conhost.exe");
        var started = DateTimeOffset.UtcNow;
        var records = new Dictionary<int, ProcessInspectionRecord>
        {
            [10] = new(10, 1, "worker", "worker.exe", started, null, ProcessInspectionStatus.Available),
            [20] = new(20, 1, "conhost", consolePath, started, null, ProcessInspectionStatus.Available),
            [30] = new(30, 1, "conhost", consolePath, started, null, ProcessInspectionStatus.AccessDenied),
            [40] = new(40, 1, "conhost", consolePath, null, null, ProcessInspectionStatus.Available)
        };
        Assert.Equal(OperatingSystem.IsWindows() ? [10, 30, 40, 50] : owned,
            ProcessObservationRoles.CommandCandidates(owned, records));
        Assert.Equal([10, 20, 30, 40, 50], owned);
        Assert.Equal(owned, ProcessObservationRoles.CommandCandidates(owned, new Dictionary<int, ProcessInspectionRecord>()));
    }

    [Fact]
    public void ConsoleInfrastructureRequiresTheWindowsSystemPath()
    {
        Assert.False(ProcessObservationRoles.IsWindowsConsoleInfrastructure(null));
        Assert.False(ProcessObservationRoles.IsWindowsConsoleInfrastructure("conhost.exe"));
        Assert.False(ProcessObservationRoles.IsWindowsConsoleInfrastructure(Path.Combine(Path.GetTempPath(), "conhost.exe")));
        if (!OperatingSystem.IsWindows()) return;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.True(ProcessObservationRoles.IsWindowsConsoleInfrastructure(Path.Combine(windows, "System32", "conhost.exe")));
        Assert.True(ProcessObservationRoles.IsWindowsConsoleInfrastructure(Path.Combine(windows, "SysWOW64", "conhost.exe")));
        Assert.False(ProcessObservationRoles.IsWindowsConsoleInfrastructure(Path.Combine(windows, "System32", "dotnet.exe")));
        Assert.False(ProcessObservationRoles.IsWindowsConsoleInfrastructure(Path.Combine(windows, "System32-other", "conhost.exe")));
    }
}
