using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Derives only engine facts supported by the snapshot, without execution.</summary>
public static class LearnedEngineSettingsDeriver
{
    public const int ShardFloor = 1;
    public const int ShardCap = 2;
    public const int TimeoutMultiplier = 3;
    public const int TimeoutFloorMinutes = 5;
    public const int TimeoutCeilingMinutes = 60;
    public const int SecondsPerMinute = 60;
    private const string QuestionSource = "derivation:learned-engine-settings";

    public static LearnedEngineSettings Derive(ProjectModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var testCount = model.Units.Count(unit => unit.IsTest.Value == true);
        var shards = model.Hazards.Count > 0 ? ShardFloor : Math.Clamp(testCount, ShardFloor, ShardCap);
        var shardReason = model.Hazards.Count > 0
            ? FormattableString.Invariant($"{model.Hazards.Count} shared-state hazards force {ShardFloor} shard")
            : FormattableString.Invariant($"{testCount} test units clamped to {ShardFloor}..{ShardCap} shards (cap {ShardCap})");

        var testUnits = model.Commands.Where(command => command.TestCommand is not null)
            .Select(command => command.UnitId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var durations = testUnits.Select(id => (Id: id,
            Seconds: model.Measurements.FirstOrDefault(measurement => measurement.UnitId == id)?.TestSeconds?.Value))
            .ToArray();
        var missing = durations.Where(duration => duration.Seconds is null).Select(duration => duration.Id).ToArray();
        var questions = new List<ProjectOwnerQuestion>();
        int? timeout = null;
        string timeoutReason;
        if (testUnits.Length == 0 || missing.Length > 0)
        {
            timeoutReason = testUnits.Length == 0 ? "No units have a test command; no measured test duration is available"
                : $"Missing measured test durations for units: {string.Join(", ", missing)}";
            questions.Add(Question("engine/timeouts/defaultMinutes",
                $"{timeoutReason}. Supply a default timeout or run project discover --measure test."));
        }
        else
        {
            // The ordinal input order breaks equal-duration ties deterministically.
            var slowest = durations.OrderByDescending(duration => duration.Seconds!.Value).First();
            var seconds = slowest.Seconds!.Value;
            var minutes = Math.Ceiling(seconds * TimeoutMultiplier / SecondsPerMinute);
            timeout = (int)Math.Clamp(minutes, TimeoutFloorMinutes, TimeoutCeilingMinutes);
            timeoutReason = FormattableString.Invariant(
                $"Slowest test unit {slowest.Id} measured {seconds} seconds; ceil({seconds} * multiplier {TimeoutMultiplier} / {SecondsPerMinute}), clamped to {TimeoutFloorMinutes}..{TimeoutCeilingMinutes} minutes");
        }

        questions.Add(Question("engine/timeouts/buildServerShutdownMinutes",
            "Discovery measures neither build-server shutdown nor discovery time. Supply a build-server shutdown timeout."));
        questions.Add(Question("engine/timeouts/discoveryMinutes",
            "Discovery measures neither build-server shutdown nor discovery time. Supply a discovery timeout."));
        return new(shards, shardReason, timeout, timeoutReason, questions.AsReadOnly());
    }

    private static ProjectOwnerQuestion Question(string key, string text) =>
        new(key, text, new FactSource(measurementReference: QuestionSource));
}
