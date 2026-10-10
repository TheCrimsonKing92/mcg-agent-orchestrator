using System.Globalization;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerExperimentReadingNarration
{
    internal static bool TryNarrate(OwnerConductEvent evt, out string sentence, out string reason,
        out string nextStep, out string ownerAction)
    {
        sentence = reason = nextStep = ownerAction = "";
        var id = OwnerActivityNarrator.Field(evt, "experiment");
        if (string.IsNullOrWhiteSpace(id)) return false;

        var prefix = id[..Math.Min(8, id.Length)];
        var trigger = OwnerActivityNarrator.Field(evt, "trigger") switch
        {
            "stop-rule" => ": stop rule reached",
            "guardrail" => ": guardrail breached",
            _ => ""
        };
        sentence = $"Experiment {prefix} reading due{trigger}";
        reason = long.TryParse(OwnerActivityNarrator.Field(evt, "observed"), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var count)
            ? $"The observed count is {count.ToString(CultureInfo.InvariantCulture)}."
            : "The observed count is not available.";
        nextStep = $"Review the reading with experiment-show {prefix}, then record the result with experiment-decide {prefix}.";
        ownerAction = $"Yes. Run experiment-show {prefix}, then record the result with experiment-decide {prefix}.";
        return true;
    }
}
