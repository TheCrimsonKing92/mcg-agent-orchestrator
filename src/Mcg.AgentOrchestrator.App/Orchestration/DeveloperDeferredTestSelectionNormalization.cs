using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class DeveloperDeferredTestSelectionNormalization
{
    internal static DeveloperDeferredTestSelection Normalize(
        DeveloperDeferredTestSelection declaration, AcceptanceGateEngineSettings settings)
    {
        var selected = new List<FindingEvidenceSelection>();
        var notRun = new List<string>(declaration.NotRun);
        foreach (var selection in declaration.Selections)
        {
            if (FindingEvidenceRequestNormalization.TryNormalize(
                    new FindingEvidenceRequest([selection]), settings, (_, _) => [],
                    out var normalized, out _, out _, out _))
                selected.AddRange(normalized.Selections);
            else
                notRun.Add(selection.TestClass);
        }
        return new DeveloperDeferredTestSelection(selected, notRun);
    }
}
