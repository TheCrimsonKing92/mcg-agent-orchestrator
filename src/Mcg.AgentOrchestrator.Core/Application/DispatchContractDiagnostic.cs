namespace Mcg.AgentOrchestrator.Core;

public static class DispatchContractDiagnostic
{
    public static bool TryExtract(string? standardError, string rule, out string diagnostic)
    {
        diagnostic = string.Empty;
        if (rule is not (DispatchFailureDiagnosticMarker.ResearcherOutputContractRejected or
            DispatchFailureDiagnosticMarker.ResearcherArtifactPersistenceFailed) ||
            string.IsNullOrWhiteSpace(standardError))
        {
            return false;
        }

        var marker = DispatchFailureDiagnosticMarker.Format(rule);
        var lines = standardError.Split('\n');
        for (var index = lines.Length - 1; index >= 0; index--)
        {
            if (!string.Equals(lines[index].Trim(), marker, StringComparison.Ordinal))
            {
                continue;
            }

            for (var earlier = index - 1; earlier >= 0; earlier--)
            {
                var line = lines[earlier].Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (line.StartsWith("Researcher output contract", StringComparison.Ordinal) ||
                    line.StartsWith("Researcher durable receipt", StringComparison.Ordinal) ||
                    line.StartsWith("Scout research output contract", StringComparison.Ordinal))
                {
                    diagnostic = line;
                    return true;
                }

                return false;
            }

            return false;
        }

        return false;
    }
}
