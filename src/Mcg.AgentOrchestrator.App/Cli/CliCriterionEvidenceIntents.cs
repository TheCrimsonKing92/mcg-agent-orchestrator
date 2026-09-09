using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

// Queues evidence requests and observes durable intent outcomes; the conductor owns their application.
internal static class CliCriterionEvidenceIntents
{
    public static void Submit(IReadOnlyList<string> args, OrchestratorWorkspace workspace,
        GoalId goalId, CliPersistentStateRunner.OperatorIntentAttribution attribution)
    {
        var values = PositionalCriterionEvidenceArguments(args);
        object payload = args[0].ToLowerInvariant() switch
        {
            OperatorIntentVerbs.CriterionEvidenceMap => BuildCriterionEvidenceMappingPayload(values),
            OperatorIntentVerbs.CriterionEvidenceRecord => BuildCriterionEvidenceReceiptPayload(values),
            _ => throw new ArgumentException($"Unsupported criterion evidence command '{args[0]}'.")
        };
        var intentId = Guid.NewGuid().ToString("N");
        var intent = new OperatorIntentRecord(
            intentId,
            ResolveFlagValue(args, "--idempotency-key") ?? intentId,
            args[0].ToLowerInvariant(),
            goalId.Value,
            TaskId: null,
            JsonSerializer.Serialize(payload, payload.GetType(), OperatorIntentJson.Options),
            PayloadFileReferences: [],
            Actor: attribution.Actor,
            Channel: attribution.Channel,
            AuthenticationAssurance: attribution.AuthenticationAssurance,
            CreatedAt: DateTimeOffset.UtcNow);
        var persisted = SqliteOperatorIntentStore
            .ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory)
            .EnqueueAsync(intent)
            .GetAwaiter()
            .GetResult();
        Console.WriteLine(
            $"Operator intent queued: id={persisted.Id} verb={persisted.Verb} goal={goalId.Value} " +
            $"status={persisted.Status}; poll with operator-intent-status {persisted.Id}.");
        if (!ConductorLoopLease.IsActive(workspace.OrchestratorDirectory))
        {
            Console.WriteLine(ConductorLoopLease.InactiveWarning);
        }

    }

    private static IReadOnlyList<string> PositionalCriterionEvidenceArguments(IReadOnlyList<string> args)
    {
        var values = new List<string>();
        for (var index = 1; index < args.Count; index++)
        {
            if (args[index] is "--goal" or "--operator-actor" or "--idempotency-key")
            {
                index++;
                continue;
            }

            if (args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unsupported flag '{args[index]}' for {args[0]}.");
            }

            values.Add(args[index]);
        }

        return values;
    }

    private static CriterionEvidenceMappingOperatorIntentPayload BuildCriterionEvidenceMappingPayload(IReadOnlyList<string> values)
    {
        var usage = CliCommandHelp.CriterionEvidenceMapUsage["Usage: ".Length..];
        if (values.Count != 6 || !int.TryParse(values[0], out var index) || !int.TryParse(values[1], out var version) ||
            !Enum.TryParse<CriterionEvidenceOwner>(values[2], true, out var owner) ||
            owner is CriterionEvidenceOwner.Worker or CriterionEvidenceOwner.Unknown)
        {
            throw new ArgumentException($"Usage: {usage}");
        }

        return new CriterionEvidenceMappingOperatorIntentPayload(index, version, owner, values[3], values[4], values[5]);
    }

    private static CriterionEvidenceReceiptOperatorIntentPayload BuildCriterionEvidenceReceiptPayload(IReadOnlyList<string> values)
    {
        var usage = CliCommandHelp.CriterionEvidenceRecordUsage["Usage: ".Length..];
        var isPassed = values.Count == 7 && values[5].Equals("passed", StringComparison.OrdinalIgnoreCase);
        var isFailed = values.Count == 7 && values[5].Equals("failed", StringComparison.OrdinalIgnoreCase);
        if (values.Count != 7 || !Enum.TryParse<CriterionEvidenceOwner>(values[1], true, out var owner) ||
            owner != CriterionEvidenceOwner.Operator || (!isPassed && !isFailed))
        {
            throw new ArgumentException($"Usage: {usage}");
        }

        return new CriterionEvidenceReceiptOperatorIntentPayload(values[0], owner, values[2], values[3], values[4], isPassed, values[6]);
    }

    public static void PrintStatus(
        IReadOnlyList<string> args,
        OrchestratorWorkspace workspace)
    {
        if (args.Count != 2)
        {
            throw new ArgumentException("Usage: operator-intent-status <intent-id>");
        }

        var databasePath = Path.Combine(
            workspace.OrchestratorDirectory,
            SqliteOperatorIntentStore.DatabaseFileName);
        if (!File.Exists(databasePath))
        {
            throw new KeyNotFoundException($"Operator intent '{args[1]}' was not found.");
        }

        var intent = SqliteOperatorIntentStore
            .OpenExisting(workspace.OrchestratorDirectory, workspace.LogDirectory)
            .GetAsync(args[1])
            .GetAwaiter()
            .GetResult()
            ?? throw new KeyNotFoundException($"Operator intent '{args[1]}' was not found.");
        Console.WriteLine(
            $"Operator intent {intent.Id}: verb={intent.Verb} goal={intent.GoalId[..Math.Min(8, intent.GoalId.Length)]} " +
            $"task={(intent.TaskId is null ? "none" : intent.TaskId[..Math.Min(8, intent.TaskId.Length)])} " +
            $"status={intent.Status} actor={intent.Actor} channel={intent.Channel} auth={intent.AuthenticationAssurance} " +
            $"outcome={intent.Outcome ?? "pending"}");
    }

    public static string? ResolveFlagValue(IReadOnlyList<string> args, string flag)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (!args[index].Equals(flag, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"{flag} requires a value.");
            }

            return args[index + 1];
        }

        return null;
    }

}
