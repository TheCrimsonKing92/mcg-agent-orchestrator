namespace Mcg.AgentOrchestrator.Infrastructure;

public static partial class WorkerProcessJobs
{
    internal enum KillProtection { NotProtected, Protected, Unverifiable }

    internal static bool IsProtectedRegistrationBoundary(
        int candidatePid, long? candidateStartTicks, ProtectedProcessIdentity? identity,
        Func<int, long?> readLiveStartTicks, ProcessAncestryLookup readAncestryFacts)
    {
        if (identity is not { } protectedIdentity ||
            !ProtectedProcessIdentity.IsLive(protectedIdentity, readLiveStartTicks))
            return false;
        if (candidatePid == protectedIdentity.ProcessId)
            return candidateStartTicks == protectedIdentity.StartTimeUtcTicks;
        return IsProtectedProcessOrAncestor(candidatePid, protectedIdentity.ProcessId, readAncestryFacts);
    }

    internal static KillProtection EvaluateKillProtection(
        int candidatePid, bool allowProtectedDescendant, ProtectedProcessIdentity? identity,
        int? rawProtectedPid, Func<int, long?> readLiveStartTicks, ProcessAncestryLookup readAncestryFacts)
    {
        var candidateTicks = candidatePid == rawProtectedPid ? readLiveStartTicks(candidatePid) : null;
        if (candidatePid == rawProtectedPid && candidateTicks is null)
            return KillProtection.Unverifiable;
        if (identity is not { } protectedIdentity ||
            !ProtectedProcessIdentity.IsLive(protectedIdentity, readLiveStartTicks))
            return KillProtection.NotProtected;
        if (candidatePid == protectedIdentity.ProcessId)
            return candidateTicks == protectedIdentity.StartTimeUtcTicks
                ? KillProtection.Protected : KillProtection.NotProtected;
        if (IsDescendantOf(protectedIdentity.ProcessId, candidatePid, readAncestryFacts) ||
            (!allowProtectedDescendant && IsDescendantOf(candidatePid, protectedIdentity.ProcessId, readAncestryFacts)))
            return KillProtection.Protected;
        return KillProtection.NotProtected;
    }

    internal static KillProtection EvaluateSweepProtection(
        int candidatePid, ProtectedProcessIdentity? identity, int? rawProtectedPid,
        Func<int, long?> readLiveStartTicks, ProcessAncestryLookup readAncestryFacts) =>
        EvaluateKillProtection(candidatePid, false, identity, rawProtectedPid,
            readLiveStartTicks, readAncestryFacts);

    internal static KillProtection EvaluateCanKillProtection(
        int candidatePid, bool allowProtectedDescendant, ProtectedProcessIdentity? identity,
        int? rawProtectedPid, Func<int, long?> readLiveStartTicks,
        ProcessAncestryLookup readAncestryFacts) =>
        EvaluateKillProtection(candidatePid, allowProtectedDescendant, identity, rawProtectedPid,
            readLiveStartTicks, readAncestryFacts);

    private static ProtectedProcessIdentity? ReadProtectedIdentity() => ProtectedProcessIdentity.ReadEnvironment();

    private static int? ReadRawProtectedPid() =>
        int.TryParse(Environment.GetEnvironmentVariable(ProtectedProcessIdentity.PidVariable),
            System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
            out var pid) && pid > 0 ? pid : null;

    private static KillProtection EvaluateLiveSweepProtection(int processId) =>
        EvaluateSweepProtection(processId, ReadProtectedIdentity(), ReadRawProtectedPid(),
            ProtectedProcessIdentity.ReadLiveStartTicks, ReadProcessAncestryFacts);

    private static KillProtection EvaluateLiveCanKillProtection(int processId, bool allowProtectedDescendant) =>
        EvaluateCanKillProtection(processId, allowProtectedDescendant, ReadProtectedIdentity(),
            ReadRawProtectedPid(), ProtectedProcessIdentity.ReadLiveStartTicks, ReadProcessAncestryFacts);

    private static void LogUnverifiableIdentity(int processId) =>
        Console.Error.WriteLine($"protected-identity-unverifiable pid={processId}");

    internal static string BuildProtectedBoundaryRegistrationFailure(int processId,
        ProtectedProcessIdentity identity) =>
        BuildRegistrationFailure(processId, "protected-process-boundary", "refused-protected-process") +
        $"; protected={identity}";
}
