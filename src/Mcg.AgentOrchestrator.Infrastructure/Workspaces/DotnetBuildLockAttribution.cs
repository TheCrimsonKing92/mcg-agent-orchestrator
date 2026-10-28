namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class DotnetBuildLockAttribution
{
    internal static ArtifactPrepLockRemediation TryRemediateArtifactPrepLock(
        DotnetBuildEnvironment environment,
        Exception exception,
        ref bool attemptedCompilerLockRemediation,
        ref bool attemptedOwnedProcessRemediation,
        out BuildLockAttribution attribution)
    {
        if (exception is AcceptanceAttemptArtifactCustodyException custody)
        {
            attribution = new BuildLockAttribution(
                custody.ArtifactsPath,
                [],
                "acceptance-attempt-custody",
                "artifact-prep",
                "prepare-artifacts");
            LockAttribution.EmitReceipt(attribution);
            return ArtifactPrepLockRemediation.SlotBusy;
        }

        var lockedPath = LockAttribution.TryExtractLockedPath(exception.ToString()) ?? environment.ArtifactsPath;
        if (LockAttribution.IsLeaseLockPath(lockedPath))
        {
            attribution = new BuildLockAttribution(lockedPath, [], "lease-lock-refused");
            return ArtifactPrepLockRemediation.Blocked;
        }

        if (DotnetBuildEnvironmentManager.TryCreateCurrentLandingFixtureAttribution(lockedPath, out attribution))
        {
            LockAttribution.EmitReceipt(attribution);
            return attribution.Source is "landing-fixture-marker"
                ? ArtifactPrepLockRemediation.SlotBusy
                : ArtifactPrepLockRemediation.RetryImmediately;
        }

        if (TryFindActiveSlotArtifactConsumer(environment) is { } activeSlotArtifactConsumer)
        {
            attribution = new BuildLockAttribution(
                lockedPath,
                [activeSlotArtifactConsumer],
                "slot-artifact-consumer",
                "artifact-prep",
                "prepare-artifacts");
            LockAttribution.EmitReceipt(attribution);
            return ArtifactPrepLockRemediation.SlotBusy;
        }

        attribution = LockAttribution.Attribute(
            lockedPath,
            environment.ArtifactsPath,
            "artifact-prep",
            "prepare-artifacts");
        if (DotnetBuildEnvironmentManager.IsCurrentLeaseSelfHeldArtifactLock(environment, attribution, currentProcessOwnsExecutionLease: true))
        {
            return ArtifactPrepLockRemediation.RetryImmediately;
        }

        if (!attemptedCompilerLockRemediation && IsCompilerLock(attribution))
        {
            attemptedCompilerLockRemediation = true;
            DotnetBuildServerRecovery.ShutdownBuildServersForCompilerLockRecovery();
            return ArtifactPrepLockRemediation.RetryImmediately;
        }

        if (IsTransientNoHolderSlotArtifactLock(environment, attribution))
        {
            return ArtifactPrepLockRemediation.SlotBusy;
        }

        if (attemptedOwnedProcessRemediation)
        {
            return ArtifactPrepLockRemediation.Blocked;
        }

        var killed = false;
        foreach (var holder in attribution.Holders
            .Where(holder =>
                holder.IsOrchestratorOwned &&
                holder.ProcessId.HasValue &&
                holder.ProcessId.Value != Environment.ProcessId)
            .DistinctBy(holder => holder.ProcessId!.Value))
        {
            killed |= WorkerProcessJobs.TryKillOrFallbackAndWait(holder.ProcessId!.Value, TimeSpan.FromSeconds(5));
        }

        attemptedOwnedProcessRemediation = killed;
        return killed ? ArtifactPrepLockRemediation.RetryImmediately : ArtifactPrepLockRemediation.Blocked;
    }

    private static bool IsCompilerLock(BuildLockAttribution attribution) =>
        attribution.Holders.Any(holder =>
            ContainsCompilerLockSignal(holder.ProcessName) ||
            ContainsCompilerLockSignal(holder.CommandLine));

    private static bool IsTransientNoHolderSlotArtifactLock(
        DotnetBuildEnvironment environment,
        BuildLockAttribution attribution) =>
        DotnetBuildLeaseInspection.PathIsUnderDirectory(attribution.Path, environment.ArtifactsPath) &&
        HasNoActionableHolder(attribution);

    private static bool HasNoActionableHolder(BuildLockAttribution attribution) =>
        attribution.Holders.Count == 0 ||
        attribution.Holders.All(holder =>
            holder.ProcessId is null &&
            (string.IsNullOrWhiteSpace(holder.ProcessName) ||
                holder.ProcessName.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
                holder.ProcessName.Equals("unknown-probe-timeout", StringComparison.OrdinalIgnoreCase)));

    internal static bool IsSlotArtifactsBusy(
        DotnetBuildEnvironment environment,
        ProcessCommandLineSnapshot? processSnapshot = null)
    {
        var snapshot = processSnapshot ?? DotnetBuildEnvironmentManager.CreateSlotCandidateProcessSnapshot();
        return TryFindActiveSlotArtifactConsumer(environment, snapshot) is not null ||
            FindUnavailableSlotCandidate(
                environment,
                snapshot,
                DotnetBuildLeaseInspection.TryReadExecutionLeaseMetadata(environment.ExecutionLockPath)) is not null ||
            snapshot.Failure is not null;
    }

    internal static BuildLockHolder? TryFindActiveSlotArtifactConsumer(
        DotnetBuildEnvironment environment,
        ProcessCommandLineSnapshot? processSnapshot = null,
        Func<int, bool>? queryProcessLiveness = null)
    {
        var artifactsPath = NormalizeForCommandLineMatch(environment.ArtifactsPath);
        if (string.IsNullOrWhiteSpace(artifactsPath))
        {
            return null;
        }

        var snapshot = processSnapshot ?? DotnetBuildEnvironmentManager.CreateSlotCandidateProcessSnapshot();
        foreach (var pair in snapshot.Records)
        {
            var record = pair.Value;
            if (pair.Key == Environment.ProcessId ||
                record.Status is ProcessInspectionStatus.Exited or ProcessInspectionStatus.DeadOrRecycled)
            {
                continue;
            }

            if (record.Status != ProcessInspectionStatus.Available ||
                string.IsNullOrWhiteSpace(record.CommandLine))
            {
                continue;
            }

            if (!CommandLineUsesSlotArtifacts(record.CommandLine, artifactsPath) ||
                !DotnetBuildLeaseInspection.IsProcessRunning(pair.Key, queryProcessLiveness))
            {
                continue;
            }

            return DotnetBuildLeaseInspection.CreateProcessHolder(pair.Key, record.CommandLine, isOrchestratorOwned: true);
        }

        return null;
    }

    internal static ProcessInspectionRecord? FindUnavailableSlotCandidate(
        DotnetBuildEnvironment environment,
        ProcessCommandLineSnapshot snapshot,
        DotnetBuildLeaseInspection.ExecutionLeaseMetadata? metadata)
    {
        if (metadata is null ||
            metadata.OwnerProcessId == Environment.ProcessId ||
            string.IsNullOrWhiteSpace(metadata.SlotOwnerToken) ||
            !metadata.SlotOwnerToken.Equals(
                environment.SlotOwnerToken,
                StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(metadata.MachineName) &&
                !metadata.MachineName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) ||
            !snapshot.Records.TryGetValue(metadata.OwnerProcessId, out var record) ||
            !DotnetBuildLeaseInspection.IsProcessRunning(metadata.OwnerProcessId))
        {
            return null;
        }

        var unavailable =
            record.Status is not ProcessInspectionStatus.Exited and not ProcessInspectionStatus.DeadOrRecycled &&
            (record.Status != ProcessInspectionStatus.Available ||
                string.IsNullOrWhiteSpace(record.CommandLine));
        return unavailable ? record : null;
    }

    private static bool CommandLineUsesSlotArtifacts(string commandLine, string artifactsPath)
    {
        if (string.IsNullOrWhiteSpace(commandLine) ||
            !NormalizeForCommandLineMatch(commandLine).Contains(artifactsPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return commandLine.Contains("testhost", StringComparison.OrdinalIgnoreCase) ||
            commandLine.Contains("vstest.console", StringComparison.OrdinalIgnoreCase) ||
            commandLine.Contains("datacollector", StringComparison.OrdinalIgnoreCase) ||
            (commandLine.Contains("dotnet", StringComparison.OrdinalIgnoreCase) &&
                commandLine.Contains(" test ", StringComparison.OrdinalIgnoreCase)) ||
            commandLine.Contains("--artifacts-path", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeForCommandLineMatch(string value) =>
        value.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    private static bool ContainsCompilerLockSignal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("VBCSCompiler", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("MSBuild", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("csc", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("build-server", StringComparison.OrdinalIgnoreCase);
    }

    internal static string FormatBuildLockHolder(BuildLockHolder? holder)
    {
        if (holder is null)
        {
            return "unknown";
        }

        var pid = holder.ProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
        var name = string.IsNullOrWhiteSpace(holder.ProcessName) ? "unknown" : holder.ProcessName;
        return $"pid-{pid}:name-{name}";
    }

    internal enum ArtifactPrepLockRemediation
    {
        Blocked,
        RetryImmediately,
        SlotBusy
    }
}
