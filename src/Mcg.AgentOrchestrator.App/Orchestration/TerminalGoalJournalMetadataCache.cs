using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal readonly record struct TerminalGoalJournalMetadata(bool IsLanded, bool IsRetiredDisposition);

internal readonly record struct TerminalGoalJournalMetadataTiming(long ElapsedMilliseconds, int JournalsRead);

internal static class TerminalGoalJournalMetadataCache
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, CacheEntry> Entries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly AsyncLocal<Measurement?> CurrentMeasurement = new();

    internal static void BeginMeasurement() => CurrentMeasurement.Value = new Measurement();

    internal static TerminalGoalJournalMetadataTiming CompleteMeasurement()
    {
        var measurement = CurrentMeasurement.Value;
        CurrentMeasurement.Value = null;
        return measurement is null
            ? default
            : new TerminalGoalJournalMetadataTiming(
                (long)TimeSpan.FromTicks(measurement.ElapsedTicks).TotalMilliseconds,
                measurement.JournalsRead);
    }

    internal static TerminalGoalJournalMetadata Read(string executionDirectory, GoalId goalId)
    {
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            lock (Gate)
            {
                var key = $"{Path.GetFullPath(executionDirectory)}|{goalId.Value}";
                var path = GoalOperationJournal.ResolveReadPath(executionDirectory, goalId);
                if (path is null)
                {
                    Entries[key] = CacheEntry.Missing;
                    return default;
                }

                var file = new FileInfo(path);
                file.Refresh();
                if (!file.Exists)
                {
                    Entries[key] = CacheEntry.Missing;
                    return default;
                }

                var identity = new FileIdentity(path, file.Length, file.LastWriteTimeUtc);
                if (Entries.TryGetValue(key, out var cached) && cached.Identity == identity)
                {
                    return cached.Metadata;
                }

                var journal = GoalOperationJournal.Read(executionDirectory, goalId);
                var metadata = new TerminalGoalJournalMetadata(
                    GoalOperationJournal.HasDurableLandingIntent(journal),
                    GoalOperationJournal.HasRetiredTerminalDisposition(journal));
                Entries[key] = new CacheEntry(identity, metadata);
                if (CurrentMeasurement.Value is { } measurement)
                {
                    measurement.JournalsRead++;
                }

                return metadata;
            }
        }
        finally
        {
            if (CurrentMeasurement.Value is { } measurement)
            {
                measurement.ElapsedTicks += Stopwatch.GetElapsedTime(startedAt).Ticks;
            }
        }
    }

    private sealed class Measurement
    {
        internal long ElapsedTicks { get; set; }
        internal int JournalsRead { get; set; }
    }

    private readonly record struct FileIdentity(string Path, long Length, DateTime LastWriteTimeUtc);

    private sealed record CacheEntry(FileIdentity? Identity, TerminalGoalJournalMetadata Metadata)
    {
        internal static readonly CacheEntry Missing = new(null, default);
    }
}
