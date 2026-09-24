using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public enum StewardShadowEscalationClass
{
    PlannerOutputContractRejected,
    DeveloperNoChangeAfterCommit,
    UntouchedTestKnownFlake,
    ObligationBoundToPreRebaseCommit
}

public static class StewardShadowEscalationKinds
{
    public const string PlannerOutputContractRejected = "planner-output-contract-rejected";
    public const string RequiredFileChangeEvidenceMissing = "required-file-change-evidence-missing";
    public const string IncompleteScopeDeclaration = "incomplete-scope-declaration";
    public const string GateFailureUntouchedTest = "gate-failure-untouched-test";
    public const string ObligationBoundToPreRebaseCommit = "obligation-bound-to-pre-rebase-commit";
}

public sealed record StewardShadowAdjudicationOptions(
    IReadOnlyList<string> KnownFlakeSignatures,
    TimeSpan AgreementWindow)
{
    public static StewardShadowAdjudicationOptions Default { get; } = new([], TimeSpan.FromDays(14));
}

public sealed record StewardShadowDecision(
    string Verb,
    IReadOnlyList<string> FollowUpVerbs,
    string GoalId,
    string TaskId,
    string? Cause,
    bool Mechanical,
    string Text);

public sealed record StewardShadowRecommendation(
    string Id,
    StewardShadowEscalationClass Class,
    string EscalationId,
    StewardShadowDecision Decision,
    string InputsHash,
    DateTimeOffset RecordedAt);

public enum StewardShadowAgreementOutcome { Match, Mismatch, NotComparable }
public enum StewardShadowDifferingField { Verb, Target, Cause }

public sealed record StewardShadowAgreementRecord(
    string RecommendationId,
    string RecommendationInputsHash,
    StewardShadowEscalationClass Class,
    string OperatorIntentId,
    string OperatorVerb,
    StewardShadowAgreementOutcome Outcome,
    IReadOnlyList<StewardShadowDifferingField> DifferingFields,
    StewardShadowDifferingField? PrimaryDifferingField,
    string? NotComparableReason,
    DateTimeOffset ResolvedAt);

public sealed record StewardObservedOperatorIntent(
    string Id,
    string Verb,
    string GoalId,
    string? TaskId,
    string PayloadJson,
    DateTimeOffset CreatedAt,
    OperatorActorKind ActorKind);

// The read port intentionally has no command or submission method.
public interface IStewardOperatorIntentReader
{
    Task<IReadOnlyList<StewardObservedOperatorIntent>> ListForGoalAsync(
        string goalId, CancellationToken cancellationToken = default);
}

public sealed class InMemoryStewardOperatorIntentReader : IStewardOperatorIntentReader
{
    private readonly List<StewardObservedOperatorIntent> _intents = [];
    public void AddObserved(StewardObservedOperatorIntent intent) => _intents.Add(intent);
    public Task<IReadOnlyList<StewardObservedOperatorIntent>> ListForGoalAsync(
        string goalId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StewardObservedOperatorIntent>>(
            _intents.Where(x => x.GoalId == goalId).ToList());
}

public sealed class SqliteStewardOperatorIntentReader : IStewardOperatorIntentReader
{
    private readonly string _dbPath;

    public SqliteStewardOperatorIntentReader(string dbPath) => _dbPath = Path.GetFullPath(dbPath);

    public static SqliteStewardOperatorIntentReader ForDirectory(string orchestratorDirectory) =>
        new(Path.Combine(orchestratorDirectory, "operator-intents.db"));

    public async Task<IReadOnlyList<StewardObservedOperatorIntent>> ListForGoalAsync(
        string goalId, CancellationToken cancellationToken = default)
    {
        // The Steward can only execute a SELECT against an existing database.
        await using var connection = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly;Pooling=False;");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, verb, goal_id, task_id, payload_json, created_at, actor_kind
            FROM operator_intents WHERE goal_id = $goal_id ORDER BY created_at, id
            """;
        command.Parameters.AddWithValue("$goal_id", goalId);
        var observed = new List<StewardObservedOperatorIntent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            observed.Add(new StewardObservedOperatorIntent(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5)), reader.IsDBNull(6)
                    ? OperatorActorKind.Human
                    : Enum.Parse<OperatorActorKind>(reader.GetString(6), ignoreCase: true)));
        return observed;
    }
}

public static class StewardShadowEscalationClassifier
{
    private static readonly Regex CommitLine = new(@"(?im)^commit:\s*([a-f0-9]{7,40})\s*$", RegexOptions.Compiled);
    private static readonly Regex PassedCommit = new(@"(?i)\bpassed-commit=([a-f0-9]{7,40})\b", RegexOptions.Compiled);

    public static StewardShadowEscalationClass? TryClassify(
        StewardEscalationItem item,
        StewardBriefingBundle bundle,
        StewardShadowAdjudicationOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(item.GoalId) || string.IsNullOrWhiteSpace(item.TaskId))
            return null;
        options ??= StewardShadowAdjudicationOptions.Default;
        var matches = new List<StewardShadowEscalationClass>();
        if (EqualsKind(item.Kind, StewardShadowEscalationKinds.PlannerOutputContractRejected))
            matches.Add(StewardShadowEscalationClass.PlannerOutputContractRejected);
        if ((IsNoChange(item.Kind) || IsNoChange(item.CauseFingerprint)) &&
            bundle.WorkerProse.Any(quote =>
                quote.Label.Contains(item.TaskId, StringComparison.OrdinalIgnoreCase) &&
                CommitLine.IsMatch(quote.Quote)))
            matches.Add(StewardShadowEscalationClass.DeveloperNoChangeAfterCommit);
        if (EqualsKind(item.Kind, StewardShadowEscalationKinds.GateFailureUntouchedTest) &&
            options.KnownFlakeSignatures.Contains(item.CauseFingerprint, StringComparer.OrdinalIgnoreCase))
            matches.Add(StewardShadowEscalationClass.UntouchedTestKnownFlake);
        if (EqualsKind(item.Kind, StewardShadowEscalationKinds.ObligationBoundToPreRebaseCommit) &&
            PassedCommit.IsMatch(item.EvidenceSummary))
            matches.Add(StewardShadowEscalationClass.ObligationBoundToPreRebaseCommit);
        return matches.Count == 1 ? matches[0] : null;
    }

    public static string? GetPassedCommit(string evidence) => PassedCommit.Match(evidence) is { Success: true } match
        ? match.Groups[1].Value : null;

    private static bool IsNoChange(string value) =>
        EqualsKind(value, StewardShadowEscalationKinds.RequiredFileChangeEvidenceMissing) ||
        EqualsKind(value, StewardShadowEscalationKinds.IncompleteScopeDeclaration);

    private static bool EqualsKind(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}

public static class StewardShadowDecisionComposer
{
    public static StewardShadowDecision Compose(
        StewardShadowEscalationClass kind,
        StewardEscalationItem item,
        StewardBriefingBundle bundle)
    {
        var taskId = item.TaskId ?? throw new ArgumentException("Shadow decision requires a task target.");
        return kind switch
        {
            StewardShadowEscalationClass.PlannerOutputContractRejected => new(
                OperatorIntentVerbs.Retry, [], item.GoalId, taskId,
                nameof(RetryCause.ContractClarification), false,
                $"Retry Planner after contract rejection. Persisted diagnostic: {item.EvidenceSummary}"),
            StewardShadowEscalationClass.DeveloperNoChangeAfterCommit => new(
                OperatorIntentVerbs.Progress, [OperatorIntentVerbs.VerifyManual], item.GoalId, taskId,
                null, false,
                $"Mark completed and verify manually. WORKER_RESULT: {string.Join(" ", bundle.WorkerProse.Where(x => x.Label.Contains(taskId, StringComparison.OrdinalIgnoreCase)).Select(x => x.Quote))}"),
            StewardShadowEscalationClass.UntouchedTestKnownFlake => new(
                OperatorIntentVerbs.Retry, [OperatorIntentVerbs.Progress, OperatorIntentVerbs.VerifyManual],
                item.GoalId, taskId, nameof(RetryCause.UnchangedContextRepeat), true,
                $"Mechanical reopen for known untouched-test flake {item.CauseFingerprint}; then progress and verify manually. {item.EvidenceSummary}"),
            StewardShadowEscalationClass.ObligationBoundToPreRebaseCommit => new(
                OperatorIntentVerbs.CriterionEvidenceMap, [], item.GoalId, taskId,
                null, false,
                $"Map each gate-owned obligation to passed commit {StewardShadowEscalationClassifier.GetPassedCommit(item.EvidenceSummary)}. {item.EvidenceSummary}"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }
}

public static class StewardShadowAgreementComparer
{
    private static readonly HashSet<string> ComparableVerbs = [
        OperatorIntentVerbs.Retry, OperatorIntentVerbs.Progress, OperatorIntentVerbs.VerifyManual,
        OperatorIntentVerbs.CriterionEvidenceMap, OperatorIntentVerbs.Adjudicate];

    public static StewardShadowAgreementRecord Compare(
        StewardShadowRecommendation recommendation,
        StewardObservedOperatorIntent intent)
    {
        string? excluded = intent.TaskId is not null && intent.TaskId != recommendation.Decision.TaskId
            ? "different-task"
            : !ComparableVerbs.Contains(intent.Verb) ? "verb-outside-vocabulary" : null;
        var differences = new List<StewardShadowDifferingField>();
        if (excluded is null)
        {
            if (!string.Equals(recommendation.Decision.Verb, intent.Verb, StringComparison.Ordinal))
                differences.Add(StewardShadowDifferingField.Verb);
            if (recommendation.Decision.GoalId != intent.GoalId || recommendation.Decision.TaskId != intent.TaskId)
                differences.Add(StewardShadowDifferingField.Target);
            if (!string.Equals(recommendation.Decision.Cause, ReadCause(intent), StringComparison.Ordinal))
                differences.Add(StewardShadowDifferingField.Cause);
        }
        return new StewardShadowAgreementRecord(
            recommendation.Id, recommendation.InputsHash, recommendation.Class, intent.Id, intent.Verb,
            excluded is not null ? StewardShadowAgreementOutcome.NotComparable :
                differences.Count == 0 ? StewardShadowAgreementOutcome.Match : StewardShadowAgreementOutcome.Mismatch,
            differences, differences.Count == 0 ? null : differences[0], excluded, intent.CreatedAt);
    }

    private static string? ReadCause(StewardObservedOperatorIntent intent)
    {
        var property = intent.Verb == OperatorIntentVerbs.Retry ? "retryCause" :
            intent.Verb == OperatorIntentVerbs.Adjudicate ? "cause" : null;
        if (property is null) return null;
        try
        {
            using var json = JsonDocument.Parse(intent.PayloadJson);
            if (json.RootElement.ValueKind != JsonValueKind.Object) return "<unparseable>";
            foreach (var field in json.RootElement.EnumerateObject())
            {
                if (!field.Name.Equals(property, StringComparison.OrdinalIgnoreCase)) continue;
                if (field.Value.ValueKind == JsonValueKind.Null) return null;
                if (field.Value.ValueKind == JsonValueKind.String)
                {
                    var value = field.Value.GetString();
                    return Enum.TryParse<RetryCause>(value, true, out var cause) ? cause.ToString() : "<unparseable>";
                }
                if (field.Value.ValueKind == JsonValueKind.Number && field.Value.TryGetInt32(out var number) &&
                    Enum.IsDefined(typeof(RetryCause), number))
                    return ((RetryCause)number).ToString();
                return "<unparseable>";
            }
            return "<unparseable>";
        }
        catch (JsonException) { return "<unparseable>"; }
    }
}

public sealed record StewardShadowAgreementWindowRate(
    int Matches, int Mismatches, int NotComparable, int N, double? Rate);

public sealed record StewardShadowClassAgreementRate(
    StewardShadowEscalationClass Class,
    StewardShadowAgreementWindowRate AllTime,
    StewardShadowAgreementWindowRate Trailing14Days,
    int Pending);

public static class StewardShadowAgreementRateCalculator
{
    public static IReadOnlyList<StewardShadowClassAgreementRate> Compute(
        IReadOnlyList<StewardShadowRecommendation> recommendations,
        IReadOnlyList<StewardShadowAgreementRecord> agreements,
        DateTimeOffset now,
        TimeSpan? window = null)
    {
        var resolved = agreements.GroupBy(x => x.RecommendationId).ToDictionary(x => x.Key, x => x.First());
        return Enum.GetValues<StewardShadowEscalationClass>().Select(kind =>
        {
            var own = recommendations.Where(x => x.Class == kind).ToList();
            var all = own.Where(x => resolved.ContainsKey(x.Id)).Select(x => resolved[x.Id]).ToList();
            var recent = own.Where(x => x.RecordedAt >= now - (window ?? TimeSpan.FromDays(14)) &&
                                        resolved.ContainsKey(x.Id)).Select(x => resolved[x.Id]).ToList();
            return new StewardShadowClassAgreementRate(kind, Rate(all), Rate(recent),
                own.Count(x => !resolved.ContainsKey(x.Id)));
        }).ToList();
    }

    private static StewardShadowAgreementWindowRate Rate(IReadOnlyList<StewardShadowAgreementRecord> records)
    {
        var match = records.Count(x => x.Outcome == StewardShadowAgreementOutcome.Match);
        var mismatch = records.Count(x => x.Outcome == StewardShadowAgreementOutcome.Mismatch);
        var excluded = records.Count(x => x.Outcome == StewardShadowAgreementOutcome.NotComparable);
        var n = match + mismatch;
        return new(match, mismatch, excluded, n, n == 0 ? null : (double)match / n);
    }
}

public sealed record StewardShadowWorkResult(bool Faulted, Exception? Error = null);

public sealed class StewardShadowAdjudicator
{
    private readonly IStewardShadowRecommendationStore _store;
    private readonly IStewardOperatorIntentReader _reader;
    private readonly StewardShadowAdjudicationOptions _options;

    public StewardShadowAdjudicator(
        IStewardShadowRecommendationStore store,
        IStewardOperatorIntentReader reader,
        StewardShadowAdjudicationOptions? options = null)
    {
        _store = store;
        _reader = reader;
        _options = options ?? StewardShadowAdjudicationOptions.Default;
    }

    public async Task<StewardShadowWorkResult> RecordAsync(
        StewardBriefingBundle bundle, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        try
        {
            foreach (var item in bundle.Escalations)
            {
                var kind = StewardShadowEscalationClassifier.TryClassify(item, bundle, _options);
                if (kind is null) continue;
                var hash = bundle.InputsHash();
                var idBytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{hash}\n{item.Id}\n{kind}"));
                var receipt = new StewardShadowRecommendation(
                    "steward-shadow-" + Convert.ToHexString(idBytes).ToLowerInvariant(), kind.Value,
                    item.Id, StewardShadowDecisionComposer.Compose(kind.Value, item, bundle), hash, now);
                await _store.AppendRecommendationAsync(receipt, cancellationToken);
            }
            return new(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new(true, ex); }
    }

    public async Task<StewardShadowWorkResult> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var recommendations = await _store.ListRecommendationsAsync(cancellationToken);
            var agreements = await _store.ListAgreementsAsync(cancellationToken);
            var resolved = agreements.Select(x => x.RecommendationId).ToHashSet();
            var usedIntents = agreements.Select(x => x.OperatorIntentId).ToHashSet();
            var latest = recommendations.Where(x => !resolved.Contains(x.Id))
                .GroupBy(x => (x.Decision.GoalId, x.Decision.TaskId))
                .Select(x => x.OrderByDescending(r => r.RecordedAt).ThenByDescending(r => r.Id, StringComparer.Ordinal).First());
            foreach (var recommendation in latest)
            {
                var intents = await _reader.ListForGoalAsync(recommendation.Decision.GoalId, cancellationToken);
                var intent = intents.Where(x => !usedIntents.Contains(x.Id) &&
                                                x.ActorKind == OperatorActorKind.Human &&
                                                x.CreatedAt >= recommendation.RecordedAt)
                    .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id, StringComparer.Ordinal).FirstOrDefault();
                if (intent is null) continue;
                await _store.AppendAgreementAsync(
                    StewardShadowAgreementComparer.Compare(recommendation, intent), cancellationToken);
                usedIntents.Add(intent.Id);
            }
            return new(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new(true, ex); }
    }

    public async Task<IReadOnlyList<StewardShadowClassAgreementRate>> RatesAsync(
        DateTimeOffset now, CancellationToken cancellationToken = default) =>
        StewardShadowAgreementRateCalculator.Compute(
            await _store.ListRecommendationsAsync(cancellationToken),
            await _store.ListAgreementsAsync(cancellationToken), now, _options.AgreementWindow);
}
