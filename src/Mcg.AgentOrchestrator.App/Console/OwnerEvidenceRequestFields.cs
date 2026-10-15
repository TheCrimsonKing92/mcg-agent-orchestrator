namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerEvidenceRequestFields
{
    internal static IReadOnlyList<(string Label, string Value)>? Read(string question)
    {
        const string request = "Planner evidence request for criterion ";
        const string availability = ". Availability: ";
        const string reason = ". Reason: ";
        const string owner = ". Owner: ";
        var line = question.Split(['\r', '\n'], 2)[0];
        if (!line.StartsWith(request, StringComparison.Ordinal) ||
            line.IndexOf(request, request.Length, StringComparison.Ordinal) >= 0 ||
            line.Contains("Merged evidence requests:", StringComparison.Ordinal)) return null;
        var colon = line.IndexOf(": ", request.Length, StringComparison.Ordinal);
        var availableAt = line.IndexOf(availability, StringComparison.Ordinal);
        if (colon <= request.Length || availableAt < colon + 2) return null;
        var reasonAt = line.IndexOf(reason, availableAt + availability.Length, StringComparison.Ordinal);
        if (reasonAt < 0) return null;
        var availabilityText = line[(availableAt + availability.Length)..reasonAt];
        var ownerAt = availabilityText.IndexOf(owner, StringComparison.Ordinal);
        var fields = new List<(string Label, string Value)>
        {
            ("Request", line[..colon]),
            ("Evidence needed", line[(colon + 2)..availableAt]),
            ("Availability", ownerAt < 0 ? availabilityText : availabilityText[..ownerAt])
        };
        if (ownerAt >= 0) fields.Add(("Evidence owner", availabilityText[(ownerAt + owner.Length)..]));
        fields.Add(("Reason", line[(reasonAt + reason.Length)..]));
        return fields;
    }
}
