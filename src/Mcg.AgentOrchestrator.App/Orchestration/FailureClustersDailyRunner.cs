using System.Globalization;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// One instance per conduct command; the state file's exclusive handle serializes relaunch overlap.
internal sealed class FailureClustersDailyRunner(
    Func<DateTimeOffset, FailureClusterInputs> source, string conductEventsLogPath, string statePath,
    Func<DateTimeOffset>? utcNow = null, Func<Func<Task>, Task>? schedule = null)
{
    internal const string StateFileName = "failure-clusters-daily-state.json";
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    private readonly Func<Func<Task>, Task> _schedule = schedule ?? (work => Task.Run(work));
    private Task? _currentRun;
    private DateOnly? _lastStarted;

    internal static FailureClustersDailyRunner ForWorkspace(OrchestratorWorkspace workspace) =>
        new(until => FailureClusterSourceReader.Read(workspace, until), workspace.ConductEventsLogPath,
            Path.Combine(workspace.OrchestratorDirectory, StateFileName));

    internal Task? CurrentRun { get { lock (_gate) return _currentRun; } }

    internal bool OnTick()
    {
        lock (_gate)
        {
            if (_currentRun is { IsCompleted: false }) return false;
            var now = _utcNow().ToUniversalTime();
            var day = DateOnly.FromDateTime(now.UtcDateTime);
            if (_lastStarted is { } last && last >= day) return false;
            _lastStarted = day;
            _currentRun = _schedule(() => RunOnceAsync(now, day));
            return true;
        }
    }

    internal async Task WaitForCurrentRunAsync()
    {
        if (CurrentRun is { } run) await run.ConfigureAwait(false);
    }

    private Task RunOnceAsync(DateTimeOffset now, DateOnly day)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            using var state = new FileStream(statePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            string? remembered = null;
            if (state.Length > 0)
            {
                using var json = JsonDocument.Parse(state);
                remembered = json.RootElement.GetProperty("lastEmittedDay").GetString();
                if (!DateOnly.TryParseExact(remembered, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var previous)) throw new InvalidDataException("Invalid lastEmittedDay.");
                if (previous >= day) return Task.CompletedTask;
            }
            var stamp = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            // Recover an append followed by a failed state save: the event log is the emission receipt.
            var emitted = false;
            var skipped = 0;
            foreach (var path in FailureClusterSourceReader.ConductPaths(conductEventsLogPath))
                FailureClusterSourceReader.ReadJsonLines(path, e =>
                {
                    if (e.GetProperty("eventKind").GetString() == FailureClusterReport.DailyEventKind &&
                        e.GetProperty("detail").GetString()!.StartsWith("FAILURE_CLUSTERS_DAILY day=" + stamp + " ", StringComparison.Ordinal))
                        emitted = true;
                }, ref skipped);
            if (!emitted)
            {
                var until = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
                var rows = source(until).Build(until.AddDays(-14), until).Take(5);
                var top = string.Join(',', rows.Select(row => row.Key + ":" + row.TotalCost.ToString("0.##", CultureInfo.InvariantCulture)));
                new ConductEventLogWriter(conductEventsLogPath).Append(FailureClusterReport.DailyEventKind,
                    null, $"FAILURE_CLUSTERS_DAILY day={stamp} top={top}", now);
            }
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { lastEmittedDay = stamp }));
            state.Position = 0;
            state.Write(bytes);
            state.SetLength(bytes.Length);
            state.Flush(flushToDisk: true);
        }
        catch (Exception ex)
        {
            // Advisory failures are observable and never fault the conductor tick or its exit join.
            Console.Error.WriteLine($"FAILURE_CLUSTERS_DAILY_FAILED day={day:yyyy-MM-dd} exception={ex.GetType().Name}: {ex.Message}");
        }
        return Task.CompletedTask;
    }
}
