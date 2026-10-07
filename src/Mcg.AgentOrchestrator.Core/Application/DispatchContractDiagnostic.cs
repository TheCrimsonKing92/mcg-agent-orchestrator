namespace Mcg.AgentOrchestrator.Core;

public static class DispatchContractDiagnostic
{
    public static bool TryExtract(string? standardError, string rule, out string diagnostic)
    {
        diagnostic = string.Empty;
        if (string.Equals(rule, DispatchFailureDiagnosticMarker.PlannerOutputContractRejected, StringComparison.Ordinal))
        {
            return TryExtractPlannerRejection(standardError, out diagnostic);
        }

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

    private static bool TryExtractPlannerRejection(string? standardError, out string diagnostic)
    {
        diagnostic = string.Empty;
        if (string.IsNullOrWhiteSpace(standardError))
        {
            return false;
        }

        var marker = DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.PlannerOutputContractRejected);
        var lines = standardError.Split('\n');
        var markerIndex = Array.FindLastIndex(lines, line =>
            string.Equals(line.Trim(), marker, StringComparison.Ordinal));
        if (markerIndex < 0)
        {
            return false;
        }

        var diagnosticLines = new List<string>();
        for (var index = markerIndex + 1; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (diagnosticLines.Count > 0 && line.StartsWith("@@MCG_", StringComparison.Ordinal))
            {
                break;
            }

            if (diagnosticLines.Count == 0 &&
                !line.StartsWith("Planner output contract failed.", StringComparison.Ordinal) &&
                !line.StartsWith("Planner durable receipt failed revalidation:", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.Length > 0)
            {
                diagnosticLines.Add(line);
            }
        }

        if (diagnosticLines.Count == 0)
        {
            return false;
        }

        diagnostic = string.Join(" ", diagnosticLines);
        if (diagnostic.Length > 1000)
        {
            diagnostic = diagnostic[..999] + "…";
        }

        return true;
    }
}
