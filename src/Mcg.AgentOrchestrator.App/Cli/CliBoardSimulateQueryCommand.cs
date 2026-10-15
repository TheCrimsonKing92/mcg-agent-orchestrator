using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.App.Simulation;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliBoardSimulateQueryCommand
{
    internal static bool IsBoardSimulateQueryCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].Equals("board-simulate", StringComparison.OrdinalIgnoreCase) &&
        !CliCommandHelp.IsCommandSpecificHelp(args);

    internal static void Execute(IReadOnlyList<string> args, string orchestratorDirectory,
        TextWriter output, TextWriter error, DateTimeOffset utcNow)
    {
        var day = DateOnly.FromDateTime(utcNow.UtcDateTime).AddDays(-1);
        var policy = GateSchedulingPolicy.Current;
        var json = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Invalid() => error.WriteLine(CliCommandHelp.BoardSimulateUsage);
        for (var i = 1; i < args.Count; i++)
        {
            var flag = args[i];
            if (!seen.Add(flag)) { Invalid(); return; }
            if (flag == "--json") { json = true; continue; }
            if (flag is not ("--day" or "--width" or "--train-size" or "--cohorts" or "--drop" or "--seed") ||
                ++i == args.Count) { Invalid(); return; }
            var value = args[i];
            switch (flag)
            {
                case "--day":
                    if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day) ||
                        day == DateOnly.MaxValue) { Invalid(); return; }
                    break;
                case "--width":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) || width is < 1 or > 4)
                    { Invalid(); return; }
                    policy = policy with { Width = width };
                    break;
                case "--train-size":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) || size is not (0 or 3 or 4))
                    { Invalid(); return; }
                    policy = policy with { TrainSize = size };
                    break;
                case "--cohorts":
                    if (value is not ("on" or "off")) { Invalid(); return; }
                    policy = policy with { CohortsEnabled = value == "on" };
                    break;
                case "--drop":
                    if (value is not ("newest" or "lowest-pass-rate")) { Invalid(); return; }
                    policy = policy with { DropRule = value == "newest" ? GateDropRule.Newest : GateDropRule.LowestPassRate };
                    break;
                case "--seed":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed)) { Invalid(); return; }
                    policy = policy with { Seed = seed };
                    break;
            }
        }
        var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var trace = BoardHistoryTraceReader.Read(orchestratorDirectory, start, start.AddDays(1));
        var result = GateSchedulingSimulator.Simulate(trace, policy);
        var calibration = BoardSimulationCalibration.Compare(
            trace.GateRuns.Count == 0 ? trace with { Landings = [] } : trace, result);
        if (json)
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            options.Converters.Add(new JsonStringEnumConverter());
            output.WriteLine(JsonSerializer.Serialize(new { day, policy, result, calibration, skipped = trace.Skipped }, options));
            return;
        }
        output.WriteLine(FormattableString.Invariant($"BOARD_SIMULATION day={day:yyyy-MM-dd} width={policy.Width} train_size={policy.TrainSize} cohorts={(policy.CohortsEnabled ? "on" : "off")} drop={(policy.DropRule == GateDropRule.Newest ? "newest" : "lowest-pass-rate")} seed={policy.Seed}"));
        if (trace.GateRuns.Count == 0) output.WriteLine("no recorded gate runs");
        foreach (var gate in result.Gates)
            output.WriteLine(FormattableString.Invariant($"GATE kind={gate.Kind} members={string.Join(',', gate.Members.Select(m => m.GoalId))} begin={gate.BeginAt:O} end={gate.EndAt:O} outcome={gate.Outcome} source={gate.Source}"));
        foreach (var landing in result.Landings)
            output.WriteLine(FormattableString.Invariant($"LANDING goal={landing.GoalId} at={landing.LandedAt:O}"));
        foreach (var skipped in trace.Skipped)
            output.WriteLine($"SKIPPED path={skipped.Path} reason={skipped.Reason.Replace('\r', ' ').Replace('\n', ' ')}");
        output.WriteLine(FormattableString.Invariant($"CALIBRATION recorded={calibration.RecordedCount} simulated={calibration.SimulatedCount} count_delta={calibration.CountDelta} matched={calibration.Matched.Count} max_delta_min={calibration.MaxAbsDeltaMinutes:0.###} median_delta_min={calibration.MedianAbsDeltaMinutes:0.###} verdict={calibration.Verdict}"));
    }
}
