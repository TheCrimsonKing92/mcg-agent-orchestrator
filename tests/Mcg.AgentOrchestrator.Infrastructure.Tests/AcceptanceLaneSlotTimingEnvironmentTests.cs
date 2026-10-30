using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: examines owned start info; never launches or changes process environment.
public sealed class AcceptanceLaneSlotTimingEnvironmentTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("lane-slot-env-").FullName;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ForCheck_LaneArguments_PointsTimingBesideUniqueTrx(bool mtp)
    {
        var first = StartInfo(Arguments(mtp, "alpha.trx"));
        var second = StartInfo(Arguments(mtp, "beta.trx"));
        var expectedDirectory = Path.Combine(root, "results with spaces");
        var firstPath = first.Environment[HostCapacitySlotTimingWriter.VariableName];
        var secondPath = second.Environment[HostCapacitySlotTimingWriter.VariableName];
        Assert.NotNull(firstPath);
        Assert.True(Path.IsPathFullyQualified(firstPath));
        Assert.Equal(expectedDirectory, Path.GetDirectoryName(firstPath));
        Assert.Equal(LaneSlotTimingReader.PathFor(Path.Combine(expectedDirectory, "alpha.trx")), firstPath);
        Assert.Equal(LaneSlotTimingReader.PathFor(Path.Combine(expectedDirectory, "beta.trx")), secondPath);
        Assert.NotEqual(firstPath, secondPath);
    }

    [Theory]
    [InlineData("no-results")]
    [InlineData("no-trx")]
    [InlineData("not-trx")]
    public void ForCheck_WithoutTrxDestination_DoesNotEnableTiming(string scenario)
    {
        string[] arguments = scenario switch
        {
            "no-results" => ["dotnet", "test", "--report-trx-filename", "alpha.trx"],
            "no-trx" => ["dotnet", "test", "--results-directory", "results"],
            _ => ["dotnet", "test", "--results-directory", "results", "--logger", "console"]
        };
        Assert.False(StartInfo(arguments).Environment.ContainsKey(HostCapacitySlotTimingWriter.VariableName));
    }

    private string[] Arguments(bool mtp, string trx) => mtp
        ? ["dotnet", "test.dll", "--results-directory", "results with spaces", "--report-trx", "--report-trx-filename", trx]
        : ["dotnet", "test", "--results-directory", "results with spaces", "--logger", "trx;LogFileName=" + trx];

    private ProcessStartInfo StartInfo(string[] arguments) => AcceptanceShellProcessStartInfo.ForCheck(
        arguments, root, null, null, false,
        new AcceptanceProcessRequest { ResolveBareCommand = false }, out _);

    public void Dispose() => Directory.Delete(root, recursive: true);
}
