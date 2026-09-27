internal sealed record AcceptanceLaneMembershipMismatch(
    string ClassFullName,
    string? Collection,
    IReadOnlyList<string> BaselineLanes,
    IReadOnlyList<string> ResolvedLanes);

internal static class AcceptanceLaneMembershipDiagnostics
{
    internal static string Describe(
        IEnumerable<AcceptanceLaneMembershipMismatch> mismatches,
        string guard = "Acceptance lane-membership guard")
    {
        var offenders = mismatches.OrderBy(item => item.ClassFullName, StringComparer.Ordinal).ToArray();
        var lines = new List<string> { $"{guard} found {offenders.Length} offending test class(es):" };
        lines.AddRange(offenders.Select(item =>
            $"{item.ClassFullName} | collection={item.Collection ?? "(none)"} | " +
            $"baseline={Lanes(item.BaselineLanes)} | resolved={Lanes(item.ResolvedLanes)}"));
        lines.Add("Repair: rename each class so its name contains one of its owning lane's " +
            "FullyQualifiedName~ substrings in config/acceptance-manifest.json " +
            "(for example ConductorBatchLoopTests<Suffix> for the DotnetBuildSlots collection); " +
            "do not edit the manifest.");
        return string.Join(Environment.NewLine, lines);
    }

    private static string Lanes(IEnumerable<string> names) =>
        $"[{string.Join(", ", names.OrderBy(name => name, StringComparer.Ordinal))}]";
}
