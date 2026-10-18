using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliExecutorSettingCommands
{
    internal static bool IsCommand(IReadOnlyList<string> args) => args.Count > 0 &&
        (args[0].Equals(OperatorIntentVerbs.ExecutorSetSlots, StringComparison.OrdinalIgnoreCase) ||
         args[0].Equals(OperatorIntentVerbs.FocusedEvidenceSet, StringComparison.OrdinalIgnoreCase));

    internal static int Run(IReadOnlyList<string> args, OrchestratorWorkspace workspace,
        TextWriter? output = null, TimeProvider? time = null)
    {
        var writer = output ?? Console.Out;
        time ??= TimeProvider.System;
        try
        {
            CliCommandHelp.ThrowIfInvalidFlags(args);
            args = OperatorIntentSubmissionOutput.StripJsonFlag(args, out var json);
            if (!IsCommand(args)) throw new ArgumentException("Unknown executor setting command.");
            var verb = args[0].ToLowerInvariant();
            var isSlots = verb == OperatorIntentVerbs.ExecutorSetSlots;
            var usage = isSlots ? CliCommandHelp.ExecutorSetSlotsUsage : CliCommandHelp.FocusedEvidenceSetUsage;
            var start = isSlots ? 3 : 1;
            if (args.Count < start) throw new ArgumentException(usage);
            var parsed = Parse(args, start, isSlots, usage);
            object payload;
            if (isSlots)
            {
                if (!int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var slots) ||
                    slots is < RemoteLaneExecutorConfiguration.MinimumSlots or > RemoteLaneExecutorConfiguration.MaximumSlots)
                    throw new ArgumentException("slots-out-of-range");
                var path = RemoteLaneExecutorConfiguration.ResolveStorePath(workspace.ExecutionDirectory);
                if (!RemoteLaneExecutorConfiguration.LoadExecutors(path).Any(e => e.Id == args[1]))
                    throw new ArgumentException("executor-not-found");
                payload = new ExecutorSetSlotsOperatorIntentPayload(args[1], slots);
            }
            else
            {
                var mode = parsed.GetValueOrDefault("--mode");
                if (mode is not null and not ("off" or "shadow")) throw new ArgumentException("mode-unsupported");
                int? sample = null;
                if (parsed.TryGetValue("--sample-every", out var text))
                {
                    if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ||
                        value < RemoteLaneExecutorConfiguration.MinimumSampleEvery)
                        throw new ArgumentException("sample-every-out-of-range");
                    sample = value;
                }
                if (mode is null && sample is null) throw new ArgumentException("no-setting");
                payload = new FocusedEvidenceSetOperatorIntentPayload(mode, sample);
            }
            var attribution = CliPersistentStateRunner.ResolveOperatorIntentAttribution(args,
                CliPersistentStateRunner.OperatorIntentSubmissionSource.Cli);
            var id = Guid.NewGuid().ToString("N");
            var intent = new OperatorIntentRecord(id, parsed.GetValueOrDefault("--idempotency-key") ?? id,
                verb, OperatorIntentScopes.Workspace, null,
                JsonSerializer.Serialize(payload, payload.GetType(), OperatorIntentJson.Options), [],
                attribution.Actor, attribution.Channel, attribution.AuthenticationAssurance, time.GetUtcNow(),
                ActorKind: attribution.ActorKind);
            var persisted = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory,
                workspace.LogDirectory).EnqueueAsync(intent).GetAwaiter().GetResult();
            OperatorIntentSubmissionOutput.PrintWorkspace(persisted, workspace.OrchestratorDirectory, json, writer);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Error: {error.Message}");
            return 1;
        }
    }

    private static Dictionary<string, string?> Parse(IReadOnlyList<string> args, int start, bool slots, string usage)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var index = start; index < args.Count; index++)
        {
            var flag = args[index];
            var name = flag.ToLowerInvariant();
            if (name is not ("--operator-actor" or "--actor-kind" or "--idempotency-key") &&
                (slots || name is not ("--mode" or "--sample-every")))
                throw new ArgumentException(usage);
            if (++index >= args.Count || args[index].StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(args[index]))
                throw new ArgumentException($"{flag} requires a value. {usage}");
            values[flag] = args[index];
        }
        return values;
    }
}
