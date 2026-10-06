using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

// Queues evidence requests and observes durable intent outcomes; the conductor owns their application.
internal static partial class CliCriterionEvidenceIntents
{
    public static void Submit(IReadOnlyList<string> args, OrchestratorWorkspace workspace,
        Goal goal, CliPersistentStateRunner.OperatorIntentAttribution attribution)
    {
        var verb = args[0].ToLowerInvariant();
        var target = verb is OperatorIntentVerbs.CriterionEvidenceMap or OperatorIntentVerbs.CriterionEvidenceRepair
            ? ResolveCriterionTarget(args, goal)
            : ((int Index, int Version, string Text)?)null;
        var values = PositionalCriterionEvidenceArguments(args);
        object payload = verb switch
        {
            OperatorIntentVerbs.CriterionEvidenceMap => BuildCriterionEvidenceMappingPayload(values, target!.Value.Index, target.Value.Version),
            OperatorIntentVerbs.CriterionEvidenceRecord => BuildCriterionEvidenceReceiptPayload(values),
            OperatorIntentVerbs.CriterionEvidenceRepair => BuildCriterionEvidenceRepairPayload(values, target!.Value.Index, target.Value.Version),
            _ => throw new ArgumentException($"Unsupported criterion evidence command '{args[0]}'.")
        };
        var intentId = Guid.NewGuid().ToString("N");
        var intent = new OperatorIntentRecord(
            intentId,
            ResolveFlagValue(args, "--idempotency-key") ?? intentId,
            args[0].ToLowerInvariant(),
            goal.Id.Value,
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
            $"Operator intent queued: id={persisted.Id} verb={persisted.Verb} goal={goal.Id.Value} " +
            $"status={persisted.Status}; poll with operator-intent-status {persisted.Id} (or add --wait).");
        if (target is { } resolved)
        {
            var text = resolved.Text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
            Console.WriteLine($"Target: {CriterionEvidenceObligation.DescribeCriterion(resolved.Version, resolved.Index)} {text[..Math.Min(80, text.Length)]}");
        }
        if (!ConductorLoopLease.IsActive(workspace.OrchestratorDirectory))
        {
            Console.WriteLine(ConductorLoopLease.InactiveWarning);
        }

    }

    private static (int Index, int Version, string Text) ResolveCriterionTarget(IReadOnlyList<string> args, Goal goal)
    {
        const string numberError = "Criterion numbers are 1-based, as the brief numbers them.";
        if (!args.Any(value => value.Equals("--criterion", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"{args[0].ToLowerInvariant()} takes --criterion <brief number> [--version <version>]; it no longer accepts a positional zero-based criterion index.");
        }

        var criterionValue = ReadTargetFlag(args, "--criterion")!;
        var obligationId = Regex.Match(criterionValue, @"\Acriterion-v([0-9]+)-([0-9]+)\z", RegexOptions.CultureInvariant);
        if (obligationId.Success)
        {
            var number = BigInteger.Parse(obligationId.Groups[2].Value, CultureInfo.InvariantCulture) + 1;
            var idVersion = obligationId.Groups[1].Value;
            throw new ArgumentException($"'{criterionValue}' is an obligation id; it names criterion {number} of version {idVersion}. Pass --criterion {number} --version {idVersion}.");
        }

        var versionValue = ReadTargetFlag(args, "--version");
        if (!TryParseNumber(criterionValue, out var criterionNumber) ||
            (versionValue is not null && !TryParseNumber(versionValue, out _)))
        {
            throw new ArgumentException(numberError);
        }

        var currentVersion = goal.AuthoritativeRefinedSpecVersion?.Version;
        var version = versionValue is null ? currentVersion : int.Parse(versionValue, CultureInfo.InvariantCulture);
        var specVersion = goal.RefinedSpecVersions.SingleOrDefault(item => item.Version == version);
        if (specVersion is null || (versionValue is not null && specVersion.IsSuperseded))
        {
            throw new ArgumentException($"Version {version?.ToString(CultureInfo.InvariantCulture) ?? "none"} is not an active criterion version on goal {goal.Id.Value}; its current criterion version is {currentVersion?.ToString(CultureInfo.InvariantCulture) ?? "none"}.");
        }

        var count = specVersion.Spec.AcceptanceCriteria.Count;
        if (criterionNumber > count)
        {
            throw new ArgumentException($"Criterion {criterionNumber} is not present in version {version}, which has {count} criteria numbered 1 to {count}.");
        }

        return (criterionNumber - 1, specVersion.Version, specVersion.Spec.AcceptanceCriteria[criterionNumber - 1]);
    }

    private static bool TryParseNumber(string value, out int number)
    {
        number = 0;
        return value.Length > 0 && value[0] != '0' && value.All(character => character is >= '0' and <= '9') &&
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    private static string? ReadTargetFlag(IReadOnlyList<string> args, string flag)
    {
        string? value = null;
        for (var index = 1; index < args.Count; index++)
        {
            if (!args[index].Equals(flag, StringComparison.OrdinalIgnoreCase))
                continue;
            if (value is not null || index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Criterion numbers are 1-based, as the brief numbers them.");
            value = args[++index];
        }
        return value;
    }

    private static IReadOnlyList<string> PositionalCriterionEvidenceArguments(IReadOnlyList<string> args)
    {
        var values = new List<string>();
        for (var index = 1; index < args.Count; index++)
        {
            var flag = args[index].ToLowerInvariant();
            if (args[index] is "--goal" or "--operator-actor" or "--idempotency-key" ||
                (args[0].ToLowerInvariant() is OperatorIntentVerbs.CriterionEvidenceMap or OperatorIntentVerbs.CriterionEvidenceRepair &&
                 flag is "--criterion" or "--version"))
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

    private static CriterionEvidenceMappingOperatorIntentPayload BuildCriterionEvidenceMappingPayload(IReadOnlyList<string> values, int index, int version)
    {
        var usage = CliCommandHelp.CriterionEvidenceMapUsage["Usage: ".Length..];
        if (values.Count != 4 || !Enum.TryParse<CriterionEvidenceOwner>(values[0], true, out var owner) ||
            owner is CriterionEvidenceOwner.Worker or CriterionEvidenceOwner.Unknown)
        {
            throw new ArgumentException($"Usage: {usage}");
        }

        return new CriterionEvidenceMappingOperatorIntentPayload(index, version, owner, values[1], values[2], values[3]);
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

    private static CriterionEvidenceRepairOperatorIntentPayload BuildCriterionEvidenceRepairPayload(IReadOnlyList<string> values, int index, int version)
    {
        var usage = CliCommandHelp.CriterionEvidenceRepairUsage["Usage: ".Length..];
        if (values.Count != 6 || !Enum.TryParse<CriterionEvidenceOwner>(values[1], true, out var owner) ||
            owner is CriterionEvidenceOwner.Worker or CriterionEvidenceOwner.Unknown)
        {
            throw new ArgumentException($"Usage: {usage}");
        }

        return new CriterionEvidenceRepairOperatorIntentPayload(
            values[0], index, version, owner, values[2], values[3], values[4], values[5]);
    }

    public static void PrintStatus(
        IReadOnlyList<string> args,
        OrchestratorWorkspace workspace)
    {
        var (intentId, _) = ParseStatusArguments(args);

        var databasePath = Path.Combine(
            workspace.OrchestratorDirectory,
            SqliteOperatorIntentStore.DatabaseFileName);
        if (!File.Exists(databasePath))
        {
            throw new KeyNotFoundException($"Operator intent '{intentId}' was not found.");
        }

        var exitCode = PrintStatus(args,
            SqliteOperatorIntentStore.OpenExisting(workspace.OrchestratorDirectory, workspace.LogDirectory),
            Console.Out, TimeProvider.System, TimeSpan.FromSeconds(1), Thread.Sleep);
        if (exitCode != 0)
        {
            throw new CliExitException(exitCode);
        }
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
