using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

// These are observed resolutions, not instructions to the conductor.
internal enum PanelResolutionKind
{
    OperatorRetry, ConductorRetry, AdjudicateClose, AdjudicateRoute, AdjudicateReopenRegate,
    OperatorAnswer, Landed, Cancelled, Unresolved
}

internal static partial class CliOwnerDigestJudgePanel
{
    internal static readonly IReadOnlyDictionary<string, IReadOnlySet<PanelResolutionKind>> ProvisionalMatchTable =
        new Dictionary<string, IReadOnlySet<PanelResolutionKind>>(StringComparer.Ordinal)
        {
            ["no-action"] = new HashSet<PanelResolutionKind> { PanelResolutionKind.Landed, PanelResolutionKind.AdjudicateClose },
            ["retry"] = new HashSet<PanelResolutionKind> { PanelResolutionKind.OperatorRetry, PanelResolutionKind.ConductorRetry, PanelResolutionKind.AdjudicateReopenRegate },
            ["repair"] = new HashSet<PanelResolutionKind> { PanelResolutionKind.OperatorRetry, PanelResolutionKind.ConductorRetry },
            ["request-evidence"] = new HashSet<PanelResolutionKind> { PanelResolutionKind.AdjudicateReopenRegate },
            ["ask-owner"] = new HashSet<PanelResolutionKind> { PanelResolutionKind.OperatorAnswer, PanelResolutionKind.AdjudicateRoute }
        };

    internal sealed record Judge(string Name, bool Launched, string Outcome, string? Kind, string? Owner)
    {
        internal bool Completed => Launched && Outcome != "pending";
        internal bool Valid => Completed && Outcome == "valid";
        internal bool HasAction => Valid && Kind is not null;
    }
    internal sealed record Resolution(PanelResolutionKind Kind, string Token);
    internal sealed record Case(string Id, string GoalId, string TriggerKind, DateTimeOffset TriggerAt,
        string Terminal, IReadOnlyList<Judge> Judges, Resolution Resolution);
    internal sealed record Result(IReadOnlyList<Case> Cases, int MissingTriggerTimes);
    private sealed record Fraction(int Numerator, int Denominator)
    {
        public double? Share => Denominator == 0 ? null : (double)Numerator / Denominator;
        internal string Text => $"{Numerator}/{Denominator}";
        internal string Rate => Share?.ToString("0.###", CultureInfo.InvariantCulture) ?? "n/a";
    }
    private sealed record JudgeMeasure(string Judge, Fraction ValidOutput, Fraction ProvisionalMatch);

    internal static void WriteText(TextWriter writer, Result result)
    {
        writer.WriteLine("Judge panel shadow cases");
        writer.WriteLine("Authority boundary");
        writer.WriteLine(ConductorJudgePanelPacketBuilder.AuthorityBoundary);
        foreach (var item in result.Cases)
        {
            var judges = string.Join(" | ", item.Judges.Select(judge =>
                $"{Part(judge.Name)}={judge.Outcome}" +
                (judge.Kind is null ? "" : $" {judge.Kind}/{Part(judge.Owner!)}")));
            writer.WriteLine($"{item.GoalId[..Math.Min(8, item.GoalId.Length)]} | {Part(item.TriggerKind)} | {item.TriggerAt:O} | {item.Terminal} | {judges} | {item.Resolution.Token}");
        }
        if (result.MissingTriggerTimes > 0)
            writer.WriteLine($"Panel cases without a recorded trigger time: {result.MissingTriggerTimes}");
        if (result.Cases.Count == 0)
        {
            writer.WriteLine("Judge panel shadow cases: none in window");
            return;
        }
        var measures = Measures(result);
        foreach (var measure in measures)
            writer.WriteLine($"Panel valid output | {Part(measure.Judge)} | {measure.ValidOutput.Text} | rate={measure.ValidOutput.Rate}");
        var agreement = Agreement(result);
        writer.WriteLine($"Panel next_action agreement | {agreement.Text} | share={agreement.Rate}");
        foreach (var measure in measures)
            writer.WriteLine($"Panel provisional-match | {Part(measure.Judge)} | {measure.ProvisionalMatch.Text} | share={measure.ProvisionalMatch.Rate}");
    }

    internal static void AddJson(JsonObject root, Result result)
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var section = new JsonObject
        {
            ["authorityBoundary"] = ConductorJudgePanelPacketBuilder.AuthorityBoundary,
            ["missingTriggerTimes"] = result.MissingTriggerTimes,
            ["cases"] = JsonSerializer.SerializeToNode(result.Cases.Select(item => new
            {
                item.Id, item.GoalId, item.TriggerKind, item.TriggerAt, item.Terminal,
                judges = item.Judges.Select(judge => new
                    { judge.Name, judge.Launched, judge.Outcome, nextActionKind = judge.Kind, nextActionOwner = judge.Owner }),
                resolution = item.Resolution.Token
            }), options)
        };
        if (result.Cases.Count > 0)
        {
            section["validOutput"] = JsonSerializer.SerializeToNode(Measures(result).Select(m => new
                { m.Judge, m.ValidOutput.Numerator, m.ValidOutput.Denominator, rate = m.ValidOutput.Share }), options);
            section["nextActionAgreement"] = JsonSerializer.SerializeToNode(Agreement(result), options);
            section["provisionalMatch"] = JsonSerializer.SerializeToNode(Measures(result).Select(m => new
                { m.Judge, m.ProvisionalMatch.Numerator, m.ProvisionalMatch.Denominator, share = m.ProvisionalMatch.Share }), options);
        }
        root["judgePanel"] = section;
    }

    private static IReadOnlyList<JudgeMeasure> Measures(Result result) => result.Cases
        .SelectMany(item => item.Judges.Select(judge => (Case: item, Judge: judge)))
        .GroupBy(pair => pair.Judge.Name, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
        .Select(group =>
        {
            var completed = group.Where(pair => pair.Judge.Completed).ToArray();
            var resolved = group.Where(pair => pair.Judge.HasAction &&
                pair.Case.Resolution.Kind != PanelResolutionKind.Unresolved).ToArray();
            return new JudgeMeasure(group.Key, new(completed.Count(pair => pair.Judge.Valid), completed.Length),
                new(resolved.Count(pair => ProvisionalMatchTable[pair.Judge.Kind!].Contains(pair.Case.Resolution.Kind)), resolved.Length));
        }).ToArray();

    private static Fraction Agreement(Result result)
    {
        var eligible = result.Cases.Where(item => item.Judges.Count == 2 &&
            item.Judges.All(judge => judge.HasAction)).ToArray();
        return new(eligible.Count(item => item.Judges[0].Kind == item.Judges[1].Kind), eligible.Length);
    }

    private static string Part(string value) => Regex.Replace(value.Replace('|', '/'), @"\s+", " ").Trim();
}
