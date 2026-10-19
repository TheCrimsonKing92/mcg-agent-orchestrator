using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class RemoteShadowAgreement
{
    internal const string Agree = "agree";
    internal const string Disagree = "disagree";
    internal const string Incomparable = "incomparable";
    internal const string RemotePrefix = "remote-";
    internal const string LocalUnavailable = "local-unavailable";
    internal const string LocalTimedOut = "local-timed-out";
    internal const string LocalApparatus = "local-apparatus";
    internal const string LocalNoFailingIdentities = "local-no-failing-identities";
    internal const string RemoteNoFailingIdentities = "remote-no-failing-identities";
    internal const string BothPassed = "both-passed";
    internal const string VerdictDiffers = "verdict-differs";
    internal const string SameFailingSet = "same-failing-set";
    internal const string FailingSetDiffers = "failing-set-differs";

    internal static RemoteShadowAgreementDecision Decide(RemoteLaneOutcomeCode remoteOutcome,
        IReadOnlyList<string>? remoteFailingTests, AcceptanceCheckResult? local)
    {
        if (remoteOutcome is not (RemoteLaneOutcomeCode.Accepted or RemoteLaneOutcomeCode.RemoteRed))
            return new(Incomparable, RemotePrefix + JsonNamingPolicy.KebabCaseLower.ConvertName(remoteOutcome.ToString()), null);
        if (local is null) return new(Incomparable, LocalUnavailable, null);
        if (!local.Passed && local.CompletionDecision?.TimedOut == true)
            return new(Incomparable, LocalTimedOut, null);
        if (!local.Passed && AcceptanceFailureClassifications.IsEnvironmentalApparatus(local.FailureClassification))
            return new(Incomparable, LocalApparatus, null);
        if (!local.Passed && local.FailingTestIdentities is not { Count: > 0 })
            return new(Incomparable, LocalNoFailingIdentities, null);
        if (remoteOutcome == RemoteLaneOutcomeCode.RemoteRed && remoteFailingTests is not { Count: > 0 })
            return new(Incomparable, RemoteNoFailingIdentities, null);
        var remotePassed = remoteOutcome == RemoteLaneOutcomeCode.Accepted;
        if (remotePassed && local.Passed) return new(Agree, BothPassed, true);
        if (remotePassed != local.Passed) return new(Disagree, VerdictDiffers, false);
        var remoteSet = new HashSet<string>(remoteFailingTests!.Distinct(StringComparer.Ordinal), StringComparer.Ordinal);
        return remoteSet.SetEquals(local.FailingTestIdentities!.Distinct(StringComparer.Ordinal))
            ? new(Agree, SameFailingSet, true) : new(Disagree, FailingSetDiffers, false);
    }
}

internal sealed record RemoteShadowAgreementDecision(string Agreement, string Reason, bool? Agree);
