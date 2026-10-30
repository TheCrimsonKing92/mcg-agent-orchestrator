namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class HomeBuildAdoptionDetector(
    string homeRoot, ConductorSupervisorBuildIdentity runningBuild,
    string? blockedCommit = null, Func<HomeBuildAdoptionRecord?>? readRecord = null,
    Action<string>? diagnostic = null)
{
    private string? _lastDiagnostic;
    private long _runningSequence;

    internal HomeBuildAdoptionRecord? Detect()
    {
        try
        {
            var path = HomeBuildAdoptionRecord.RecordPath(homeRoot);
            var record = readRecord is null ? HomeBuildAdoptionRecord.Read(path) : readRecord();
            if (record is null) return Unavailable("record-missing");
            if (record.SchemaVersion != 1 || record.Sequence <= 0 ||
                !HomeBuildAdoptionRecord.SamePath(record.HomeSourceRoot, homeRoot) ||
                !IsCommit(record.AdoptedCommit) || !Path.IsPathFullyQualified(record.RunDirectory) ||
                !Path.IsPathFullyQualified(record.AppDllPath) ||
                !HomeBuildAdoptionRecord.SamePath(Path.GetDirectoryName(record.AppDllPath)!, record.RunDirectory) ||
                Path.GetFileName(record.AppDllPath) != "Mcg.AgentOrchestrator.App.dll")
                return Unavailable("record-incompatible");
            if (record.Status != "adopted") return null;
            if (string.Equals(record.AdoptedCommit, runningBuild.CommitSha, StringComparison.OrdinalIgnoreCase) ||
                HomeBuildAdoptionRecord.SamePath(record.RunDirectory, runningBuild.RunDirectory))
            {
                _runningSequence = Math.Max(_runningSequence, record.Sequence);
                return null;
            }
            if (IsCommit(runningBuild.CommitSha))
            {
                var identity = HomeBuildAdoptionRecord.Read(HomeBuildAdoptionRecord.BuildIdentityPath(path, runningBuild.CommitSha));
                if (identity is not null && identity.SchemaVersion == 1 &&
                    HomeBuildAdoptionRecord.SamePath(identity.HomeSourceRoot, homeRoot) &&
                    string.Equals(identity.AdoptedCommit, runningBuild.CommitSha, StringComparison.OrdinalIgnoreCase))
                    _runningSequence = Math.Max(_runningSequence, identity.Sequence);
            }
            if (record.Sequence <= _runningSequence ||
                string.Equals(record.AdoptedCommit, blockedCommit, StringComparison.OrdinalIgnoreCase)) return null;
            if (!File.Exists(record.AppDllPath) ||
                !string.Equals(File.ReadAllText(record.AppDllPath + ".git-head").Trim(), record.AdoptedCommit, StringComparison.OrdinalIgnoreCase))
                return Unavailable("staged-build-unavailable-or-mismatched");
            _lastDiagnostic = null;
            return record;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or NotSupportedException)
        { return Unavailable(ex.GetType().Name); }
    }

    private HomeBuildAdoptionRecord? Unavailable(string reason)
    {
        if (_lastDiagnostic != reason) diagnostic?.Invoke($"HOME_BUILD_ADOPTION_UNAVAILABLE reason={reason}");
        _lastDiagnostic = reason;
        return null;
    }

    internal static bool IsCommit(string value) => value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);
}
