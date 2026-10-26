using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: pure decisions over immutable records; no process or external state.
public sealed class ProcessStopRevalidationTests
{
    [Theory]
    [InlineData("a", "identity-unavailable")]
    [InlineData("b", "identity-unavailable")]
    [InlineData("c", "inspection-unavailable")]
    [InlineData("d", "inspection-unavailable")]
    [InlineData("e", "inspection-unavailable")]
    [InlineData("f", "identity-mismatch")]
    [InlineData("g", "identity-mismatch")]
    [InlineData("h", null)]
    [InlineData("i", "command-changed")]
    [InlineData("j", null)]
    [InlineData("k", null)]
    public void Evaluate_Inspection_rows_Return_expected_reason(string row, string? expected)
    {
        var (recorded, current, markers) = InspectionRow(row);

        Assert.Equal(expected, ProcessStopRevalidation.Evaluate(recorded, current, markers));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("b")]
    [InlineData("c")]
    [InlineData("d")]
    [InlineData("e")]
    [InlineData("f")]
    [InlineData("g")]
    [InlineData("h")]
    [InlineData("i")]
    [InlineData("j")]
    [InlineData("k")]
    public void Cli_forwarder_Inspection_rows_Match_execution_decision(string row)
    {
        var (recorded, current, markers) = InspectionRow(row);

        Assert.Equal(
            ProcessStopRevalidation.Evaluate(recorded, current, markers),
            RepoProcessCliCommand.EvaluateStopRevalidation(
                ToSnapshot(recorded), current is null ? null : ToSnapshot(current), markers));
    }

    private static (ProcessInspectionRecord Recorded, ProcessInspectionRecord? Current, IReadOnlyList<string> Markers)
        InspectionRow(string row)
    {
        var baseline = new ProcessInspectionRecord(51, 1, "dotnet", @"C:\repo\tools\dotnet.exe",
            new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero),
            "dotnet App.dll conduct --loop", ProcessInspectionStatus.Available);
        string[] markers = ["conduct"];
        return row switch
        {
            "a" => (baseline with { StartedAt = null }, null, markers),
            "b" => (baseline with { ExecutablePath = "  " }, baseline, markers),
            "c" => (baseline, null, markers),
            "d" => (baseline, baseline with { Status = ProcessInspectionStatus.AccessDenied }, markers),
            "e" => (baseline, baseline with { CommandLine = null }, markers),
            "f" => (baseline, baseline with { StartedAt = baseline.StartedAt!.Value.AddMinutes(1) }, markers),
            "g" => (baseline, baseline with { ExecutablePath = @"C:\repo\other\dotnet.exe" }, markers),
            "h" => (baseline, baseline with { ExecutablePath = @"C:\REPO\TOOLS\DOTNET.EXE" }, markers),
            "i" => (baseline, baseline, ["conduct", "--watch"]),
            "j" => (baseline, baseline, ["CONDUCT"]),
            "k" => (baseline, baseline, []),
            _ => throw new ArgumentOutOfRangeException(nameof(row), row, "Unknown inspection row.")
        };
    }

    private static RepoProcessCliCommand.ProcessSnapshot ToSnapshot(ProcessInspectionRecord record) =>
        new(record.ProcessId, record.ParentProcessId, record.Name, record.ExecutablePath,
            record.StartedAt, record.CommandLine, record.Status);
}
