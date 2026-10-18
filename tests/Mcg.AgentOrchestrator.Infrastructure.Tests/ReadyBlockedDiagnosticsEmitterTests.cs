using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ReadyBlockedDiagnosticsEmitterTests
{
    [Fact]
    public void EmitPreservesDiagnosticLinesInTaskNumberOrder()
    {
        ReadyBlockedDiagnostic[] diagnostics =
        [
            new("12345678", 3, "task3", "codex", "held", ["with spaces"]),
            new("12345678", 1, "task1", "claude", "unavailable"),
            new("12345678", 2, "task2", "codex", "provider-budget-exhausted")
        ];
        var output = InfrastructureTestSupport.CaptureConsoleError(() => ReadyBlockedDiagnosticsEmitter.Emit(diagnostics));
        Assert.Equal(diagnostics.OrderBy(item => item.TaskNumber).Select(item => item.ToLine()),
            output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }
}
