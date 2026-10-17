using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Optional observations only: absent or malformed timing evidence must never affect a lane decision.
internal sealed record RemoteLanePhaseTimings(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? SubmitSeconds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? QueueSeconds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? RunnerSeconds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? WorktreeSeconds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? BuildWaitSeconds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? BuildSeconds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? BuildSkipped = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? PreambleSeconds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? TestSeconds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? CollectSeconds = null)
{
    internal static RemoteLanePhaseTimings? Compute(IReadOnlyList<RemoteLaneStep>? submissionSteps,
        JsonElement? lastStatus, IReadOnlyList<string>? trxPaths, IReadOnlyList<RemoteLaneStep>? fetches)
    {
        var timings = new RemoteLanePhaseTimings();
        try
        {
            var firstStep = Safe(() => submissionSteps?.Where(s => s.Name != "runner-log")
                .Select(s => (DateTimeOffset?)s.StartedAt).Min());
            var lastStep = Safe(() => submissionSteps?.Where(s => s.Name != "runner-log")
                .Select(s => (DateTimeOffset?)s.EndedAt).Max());
            var seconds = Number(lastStatus, "seconds");
            var utc = Safe(() => Date(Property(lastStatus, "utc")));
            var runnerStart = Safe(() => utc is { } end && seconds is { } duration
                ? (DateTimeOffset?)end.AddSeconds(-duration) : null);
            var phases = Property(lastStatus, "phases");
            timings = timings with
            {
                SubmitSeconds = Difference(lastStep, firstStep), QueueSeconds = Difference(runnerStart, lastStep),
                RunnerSeconds = Round(seconds), WorktreeSeconds = Round(Number(phases, "worktreeSeconds")),
                BuildWaitSeconds = Round(Number(phases, "buildWaitSeconds")),
                BuildSeconds = Round(Number(phases, "buildSeconds")),
                BuildSkipped = Safe(() => Property(phases, "buildSkipped") is { } skipped &&
                    skipped.ValueKind is JsonValueKind.True or JsonValueKind.False ? (bool?)skipped.GetBoolean() : null)
            };
            DateTimeOffset? earliest = null, latest = null;
            foreach (var path in trxPaths ?? [])
            {
                // Isolate each file so one bad artifact does not discard the other observations.
                try
                {
                    using var reader = XmlReader.Create(path, new XmlReaderSettings
                    { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                    while (reader.Read())
                    {
                        if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Times") continue;
                        var start = ParseDate(reader.GetAttribute("start"));
                        var finish = ParseDate(reader.GetAttribute("finish"));
                        if (start is { } s && (earliest is null || s < earliest)) earliest = s;
                        if (finish is { } f && (latest is null || f > latest)) latest = f;
                        break;
                    }
                }
                catch (Exception) { /* Unreadable evidence contributes no timing. */ }
            }
            var lastFetch = Safe(() => fetches?.Select(f => (DateTimeOffset?)f.EndedAt).Max());
            timings = timings with
            {
                PreambleSeconds = Difference(earliest, runnerStart), TestSeconds = Difference(latest, earliest),
                CollectSeconds = Difference(lastFetch, latest)
            };
        }
        catch (Exception) { /* Diagnostics cannot change acceptance, even with an unusable input collection. */ }
        return timings == new RemoteLanePhaseTimings() ? null : timings;
    }

    private static T? Safe<T>(Func<T?> read) where T : struct
    {
        try { return read(); }
        catch (Exception) { return null; }
    }

    private static JsonElement? Property(JsonElement? parent, string name) => Safe(() =>
        parent is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var child)
            ? (JsonElement?)child : null);

    private static double? Number(JsonElement? parent, string name) => Safe(() =>
        Property(parent, name) is { ValueKind: JsonValueKind.Number } number &&
        number.TryGetDouble(out var value) && double.IsFinite(value) ? (double?)value : null);

    private static DateTimeOffset? Date(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.String } text ? ParseDate(text.GetString()) : null;

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed : null;

    private static double? Round(double? value) => value is { } seconds && double.IsFinite(seconds)
        ? Math.Round(seconds, 1, MidpointRounding.AwayFromZero) : null;

    private static double? Difference(DateTimeOffset? end, DateTimeOffset? start) =>
        end is { } e && start is { } s ? Round((e - s).TotalSeconds) : null;
}
