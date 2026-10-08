using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class UnitMeasurementCollector
{
    public static IReadOnlyList<UnitMeasurement> Collect(string root, List<UnitCommands> commands,
        IUnitCommandMeasurer? measurer, UnitCommandKinds kinds, List<ProjectOwnerQuestion> questions)
    {
        if ((kinds & ~UnitCommandKinds.All) != 0)
            throw new ArgumentOutOfRangeException(nameof(kinds));
        var measurements = new List<UnitMeasurement>();
        if (measurer is null || kinds == UnitCommandKinds.None)
            return measurements;
        foreach (var unit in commands)
        {
            var build = Measure(unit, UnitCommandKinds.Build, unit.BuildCommand);
            ProjectFact<double>? test = null;
            if (kinds.HasFlag(UnitCommandKinds.Test) && unit.TestCommand is not null)
            {
                if (kinds.HasFlag(UnitCommandKinds.Build) && build is null)
                    Skip(unit, "test", unit.TestCommand, "Test measurement skipped because the requested build did not succeed.");
                else
                    test = Measure(unit, UnitCommandKinds.Test, unit.TestCommand);
            }
            if (build is not null || test is not null)
                measurements.Add(new UnitMeasurement(unit.UnitId, build, test));
        }
        return measurements;

        ProjectFact<double>? Measure(UnitCommands unit, UnitCommandKinds kind, ProjectFact<string> command)
        {
            if (!kinds.HasFlag(kind)) return null;
            var label = kind == UnitCommandKinds.Build ? "build" : "test";
            if (command.Confidence != FactConfidence.High)
            {
                Skip(unit, label, command, "Measurement skipped; confirm the command before executing it.");
                return null;
            }
            var result = measurer.Measure(root, unit.UnitId, kind, command.Value);
            if (result.Seconds is { } seconds)
                return new ProjectFact<double>(seconds, new FactSource(measurementReference:
                    $"measurement:project-discover/{unit.UnitId}/{label}"), FactConfidence.High);
            Skip(unit, label, command, $"Measurement failed: {result.Failure}");
            return null;
        }

        void Skip(UnitCommands unit, string label, ProjectFact<string> command, string reason) =>
            DotnetProjectDiscoveryAdapter.Ask($"commands/{unit.UnitId}/{label}/duration", reason, command.Source, questions);
    }
}
