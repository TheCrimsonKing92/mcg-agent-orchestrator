using System.Globalization;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ProgramHealthEvaluator
{
    internal static ProgramHealthAssessment Evaluate(ProgramHealthSnapshot snapshot, ProgramHealthThresholds thresholds)
    {
        var current = snapshot.Receipts.Where(receipt => receipt.PassedAt <= snapshot.Now &&
            string.Equals(receipt.ObservedMainRevision, snapshot.IntegrationHead, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(receipt => receipt.ReceiptId, StringComparer.Ordinal).ToArray();
        var stale = snapshot.Now - (snapshot.LastLandingAt ?? snapshot.ObservedSince);
        var minutes = Math.Max(0, Minutes(stale));
        var held = current.Where(receipt => snapshot.Now - receipt.PassedAt >= thresholds.HeldReceipt)
            .OrderBy(receipt => receipt.PassedAt).ThenBy(receipt => receipt.ReceiptId, StringComparer.Ordinal).ToArray();
        var conditions = new List<string>();
        if (held.Length > 0) conditions.Add("held-receipt");
        if (stale >= thresholds.NoLanding && (current.Length > 0 || snapshot.VerifiedGoals > 0))
            conditions.Add("no-landing");
        var top = held.FirstOrDefault();
        var topBlocker = top?.ReceiptId ?? (conditions.Count > 0 ? "verified-goals" : string.Empty);
        var topMinutes = top is not null ? Minutes(snapshot.Now - top.PassedAt) : conditions.Count > 0 ? minutes : 0;
        var diagnosis = top is not null
            ? FormattableString.Invariant($"Nothing landed for {Duration(minutes)}; {held.Length} passed {Plural("train", held.Length)} held, oldest {ShortReceipt(top.ReceiptId)} for {topMinutes} min ({string.Join(",", top.MemberGoalIds.Select(id => Prefix(id.ToLowerInvariant())))}).")
            : FormattableString.Invariant($"Nothing landed for {Duration(minutes)}; {snapshot.VerifiedGoals} Verified {Plural("goal", snapshot.VerifiedGoals)} and {current.Length} passed {Plural("train", current.Length)} waiting.");
        return new(conditions.ToArray(), topBlocker, minutes, current.Length, held.Length,
            snapshot.VerifiedGoals, topMinutes, diagnosis);
    }

    internal static string FormatAlert(ProgramHealthSnapshot snapshot, ProgramHealthAssessment assessment)
    {
        var lastLanding = snapshot.LastLandingAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) ?? "none";
        return FormattableString.Invariant($"PROGRAM_HEALTH_ALERT conditions={string.Join(",", assessment.Conditions)} topBlocker={assessment.TopBlocker} minutesSinceLanding={assessment.MinutesSinceLanding} lastLanding={lastLanding} currentReceipts={assessment.CurrentReceipts} overThreshold={assessment.OverThreshold} verifiedGoals={assessment.VerifiedGoals} diagnosis={assessment.Diagnosis}");
    }

    internal static string FormatCleared(string previousKey, ProgramHealthAssessment assessment) =>
        FormattableString.Invariant($"PROGRAM_HEALTH_CLEARED previous={previousKey} minutesSinceLanding={assessment.MinutesSinceLanding} currentReceipts={assessment.CurrentReceipts} verifiedGoals={assessment.VerifiedGoals}");

    private static int Minutes(TimeSpan duration) => (int)Math.Floor(duration.TotalMinutes);
    private static string Duration(int minutes) => minutes < 60
        ? FormattableString.Invariant($"{minutes} min")
        : FormattableString.Invariant($"{minutes / 60} h {minutes % 60} min");
    private static string Plural(string word, int count) => count == 1 ? word : word + "s";
    private static string Prefix(string id) => id[..Math.Min(8, id.Length)];
    private static string ShortReceipt(string id) => Prefix(id.StartsWith("merge-train-receipt-", StringComparison.Ordinal)
        ? id["merge-train-receipt-".Length..] : id);
}
