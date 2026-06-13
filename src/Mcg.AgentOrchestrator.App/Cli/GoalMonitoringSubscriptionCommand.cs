using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class GoalMonitoringSubscriptionCommand
{
    private const string OnceFlag = "--once";
    private const string SinceFlag = "--since";

    public static GoalMonitoringSubscriptionOptions Parse(IReadOnlyList<string> parts)
    {
        if (parts.Count < 3)
        {
            throw new ArgumentException("Usage: monitor-goal <dashboard-url> <goal-id> [--since <event-id>] [--once]");
        }

        var sinceEventId = 0L;
        var once = false;

        for (var index = 3; index < parts.Count; index++)
        {
            var part = parts[index];
            if (part.Equals(OnceFlag, StringComparison.OrdinalIgnoreCase))
            {
                once = true;
                continue;
            }

            if (part.Equals(SinceFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= parts.Count || !long.TryParse(parts[index + 1], out sinceEventId) || sinceEventId < 0)
                {
                    throw new ArgumentException("Usage: monitor-goal <dashboard-url> <goal-id> [--since <event-id>] [--once]");
                }

                index++;
                continue;
            }

            throw new ArgumentException($"Unknown monitor-goal option '{part}'.");
        }

        return new GoalMonitoringSubscriptionOptions(
            CreateDashboardUri(parts[1]),
            parts[2],
            sinceEventId,
            once);
    }

    public static Uri BuildSnapshotUri(GoalMonitoringSubscriptionOptions options)
    {
        return BuildUri(options, stream: false);
    }

    public static Uri BuildStreamUri(GoalMonitoringSubscriptionOptions options)
    {
        return BuildUri(options, stream: true);
    }

    public static async Task RunAsync(IReadOnlyList<string> parts, TextWriter output, CancellationToken cancellationToken = default)
    {
        var options = Parse(parts);
        using var client = new HttpClient();

        if (options.Once)
        {
            var batch = await client.GetFromJsonAsync<GoalMonitoringBatchDto>(
                BuildSnapshotUri(options),
                DashboardJson.Options(),
                cancellationToken).ConfigureAwait(false);
            if (batch is null)
            {
                throw new InvalidOperationException("Dashboard returned an empty monitoring response.");
            }

            PrintBatch(batch, output);
            return;
        }

        await using var stream = await client.GetStreamAsync(BuildStreamUri(options), cancellationToken).ConfigureAwait(false);
        await foreach (var serverEvent in ReadServerSentEventsAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            PrintServerSentEvent(serverEvent, output);
        }
    }

    public static void PrintBatch(GoalMonitoringBatchDto batch, TextWriter output)
    {
        PrintSnapshot(batch.Snapshot, output);
        foreach (var evt in batch.Events)
        {
            PrintTimelineEvent(evt, output);
        }
    }

    public static void PrintServerSentEvent(ServerSentEvent serverEvent, TextWriter output)
    {
        if (serverEvent.Event.Equals("goal.snapshot", StringComparison.OrdinalIgnoreCase))
        {
            var snapshot = JsonSerializer.Deserialize<GoalMonitoringSnapshotDto>(serverEvent.Data, DashboardJson.Options());
            if (snapshot is not null)
            {
                PrintSnapshot(snapshot, output);
            }

            return;
        }

        if (serverEvent.Event.Equals("timeline", StringComparison.OrdinalIgnoreCase))
        {
            var evt = JsonSerializer.Deserialize<GoalMonitoringEventDto>(serverEvent.Data, DashboardJson.Options());
            if (evt is not null)
            {
                PrintTimelineEvent(evt, output);
            }

            return;
        }

        output.WriteLine($"event {serverEvent.Event} id={serverEvent.Id ?? "-"}");
    }

    public static async IAsyncEnumerable<ServerSentEvent> ReadServerSentEventsAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? id = null;
        string? eventName = null;
        var data = new StringBuilder();

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                if (eventName is not null || data.Length > 0)
                {
                    yield return new ServerSentEvent(id, eventName ?? "message", data.ToString());
                }

                id = null;
                eventName = null;
                data.Clear();
                continue;
            }

            if (line.StartsWith(':'))
            {
                continue;
            }

            if (line.StartsWith("id:", StringComparison.Ordinal))
            {
                id = line["id:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line["event:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                {
                    data.AppendLine();
                }

                data.Append(line["data:".Length..].TrimStart());
            }
        }
    }

    private static void PrintSnapshot(GoalMonitoringSnapshotDto snapshot, TextWriter output)
    {
        var running = snapshot.Tasks.Count(task => task.Status == WorkTaskStatus.Running);
        var failed = snapshot.Tasks.Count(task => task.Status == WorkTaskStatus.Failed);
        var completed = snapshot.Tasks.Count(task => task.Status == WorkTaskStatus.Completed);
        output.WriteLine(
            $"snapshot goal={snapshot.GoalId[..Math.Min(8, snapshot.GoalId.Length)]} status={snapshot.Monitor.Status} tasks={snapshot.Tasks.Count} completed={completed} running={running} failed={failed} lastEvent={snapshot.LastEventId} attention={snapshot.Monitor.Attention.Count}");
    }

    private static void PrintTimelineEvent(GoalMonitoringEventDto evt, TextWriter output)
    {
        var scope = evt.TaskNumber is null ? "goal" : $"task {evt.TaskNumber}";
        output.WriteLine($"event {evt.Id} {evt.OccurredAt:u} {evt.Kind} {scope}: {evt.Message}");
    }

    private static Uri BuildUri(GoalMonitoringSubscriptionOptions options, bool stream)
    {
        var goalId = Uri.EscapeDataString(options.GoalId);
        var path = stream ? $"api/goals/{goalId}/events/stream" : $"api/goals/{goalId}/events";
        var builder = new UriBuilder(new Uri(options.DashboardUri, path));
        if (options.SinceEventId > 0)
        {
            builder.Query = $"since={options.SinceEventId}";
        }

        return builder.Uri;
    }

    private static Uri CreateDashboardUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Dashboard URL must be an absolute http or https URL.");
        }

        return value.EndsWith("/", StringComparison.Ordinal) ? uri : new Uri(value + "/");
    }
}

internal sealed record GoalMonitoringSubscriptionOptions(Uri DashboardUri, string GoalId, long SinceEventId, bool Once);

internal sealed record ServerSentEvent(string? Id, string Event, string Data);
