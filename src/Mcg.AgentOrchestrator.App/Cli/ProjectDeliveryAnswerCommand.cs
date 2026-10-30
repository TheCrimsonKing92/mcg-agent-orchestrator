using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ProjectDeliveryAnswerCommand
{
    internal static int Execute(OrchestratorProjectRegistry registry, OrchestratorProject project,
        string key, string value, string? reason, TextWriter output, Func<DateTimeOffset>? clock)
    {
        try
        {
            value = key == ProjectDelivery.ModeKey
                ? ProjectDelivery.ValidateMode(value) : ProjectDelivery.ValidateDecider(value);
            var utc = (clock?.Invoke() ?? DateTimeOffset.UtcNow).ToUniversalTime();
            utc = new DateTimeOffset(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
            registry.SetDeliveryAnswer(project.Name, new OwnerAnswer(key, value, reason, utc));
            output.WriteLine($"Recorded owner answer: {key} = {value} (High, {OwnerAnswerApplier.FormatRecorded(utc)})");
            return 0;
        }
        catch (ArgumentException exception)
        {
            output.WriteLine($"Refused: {exception.Message}");
            return 1;
        }
    }
}
