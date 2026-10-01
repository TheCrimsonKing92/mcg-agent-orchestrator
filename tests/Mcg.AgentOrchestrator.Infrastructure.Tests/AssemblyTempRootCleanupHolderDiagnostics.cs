using Mcg.AgentOrchestrator.Infrastructure;

internal sealed record AssemblyTempRootCleanupHolder(
    int ProcessId, int ParentProcessId, string ParentLive, string Image, string Match,
    string? CurrentDirectory, string? CommandLine);

internal sealed record AssemblyTempRootCleanupHolderReport(
    string MessageText, IReadOnlyList<string> DiagnosticLines,
    IReadOnlyList<AssemblyTempRootCleanupHolder> Holders, int UnreadableCurrentDirectoryCount);

internal static class AssemblyTempRootCleanupHolderDiagnostics
{
    internal const int MaximumCommandLineLength = 512;
    private const string Prefix = "assembly-temp-cleanup-holder";

    internal static AssemblyTempRootCleanupHolderReport Describe(string root,
        Func<ProcessCommandLineSnapshot> processSnapshot,
        Func<int, ProcessCurrentDirectoryReadResult>? currentDirectoryReader = null)
    {
        currentDirectoryReader ??= WindowsNativeProcessInspection.ReadCurrentDirectory;
        try
        {
            var normalizedRoot = NormalizeRoot(root).Replace('/', '\\');
            var snapshot = processSnapshot();
            var records = snapshot.Records;
            var failure = snapshot.Failure;
            var holders = new List<AssemblyTempRootCleanupHolder>();
            var messages = new List<string>();
            var unreadable = 0;
            foreach (var process in records.Values.Where(IsLive).OrderBy(process => process.ProcessId))
            {
                ProcessCurrentDirectoryReadResult directory;
                try { directory = currentDirectoryReader(process.ProcessId); }
                catch { directory = new(null, ProcessCurrentDirectoryStatus.ReadFailed); }
                var commandMatch = ContainsRoot(process.CommandLine);
                var executableMatch = ContainsRoot(process.ExecutablePath);
                // A read can race exit; retain links already supplied by the live snapshot.
                if (directory.Status == ProcessCurrentDirectoryStatus.ProcessExited && !commandMatch && !executableMatch) continue;

                var cwd = directory.Status == ProcessCurrentDirectoryStatus.Available ? directory.Path : null;
                if (string.IsNullOrWhiteSpace(cwd) && directory.Status != ProcessCurrentDirectoryStatus.ProcessExited) unreadable++;
                var match = IsWithinRoot(cwd, root) ? "cwd"
                    : commandMatch ? "cmd"
                    : executableMatch ? "exe" : null;
                if (match is null) continue;

                var parentLive = failure is not null ? "unknown"
                    : records.TryGetValue(process.ParentProcessId, out var parent) && IsLive(parent) ? "true" : "false";
                holders.Add(new(process.ProcessId, process.ParentProcessId, parentLive,
                    process.Name, match, cwd, process.CommandLine));
                // Preserve established cmd/exe message lines while adding cwd evidence.
                messages.Add($"pid={process.ProcessId} parent={process.ParentProcessId} image={process.Name} " +
                    (!commandMatch && !executableMatch ? $"cwd={cwd} " : string.Empty) + $"cmd={process.CommandLine}");
            }

            var lines = holders.Select(FormatHolder).ToList();
            if (holders.Count == 0)
            {
                messages.Add($"no live process command line or executable path contains '{root}'");
                lines.Add($"{Prefix} none root={Quote(root)}");
            }

            if (failure is not null)
            {
                messages.Add($"process snapshot incomplete: status={failure.Status}, operation={failure.Operation}, native_error={failure.NativeError}");
            }

            lines.Add($"{Prefix} summary snapshot={(failure is null ? "complete" : "incomplete")} unreadable_cwd_count={unreadable}");
            return new(string.Join(Environment.NewLine, messages), lines, holders, unreadable);

            bool ContainsRoot(string? value) => value?.Replace('/', '\\')
                .Contains(normalizedRoot, StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (Exception exception)
        {
            return new($"process snapshot unavailable: {exception.GetType().Name}: " +
                exception.Message.Replace('\r', ' ').Replace('\n', ' '),
                [$"{Prefix} none root={Quote(root)}",
                 $"{Prefix} summary snapshot=unavailable unreadable_cwd_count=0"], [], 0);
        }
    }

    private static bool IsLive(ProcessInspectionRecord process) =>
        process.Status is not (ProcessInspectionStatus.Exited or ProcessInspectionStatus.DeadOrRecycled);

    private static bool IsWithinRoot(string? path, string root)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var prefix = Path.EndsInDirectorySeparator(normalizedRoot)
                ? normalizedRoot : normalizedRoot + Path.DirectorySeparatorChar;
            return normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string FormatHolder(AssemblyTempRootCleanupHolder holder)
    {
        var truncated = holder.CommandLine?.Length > MaximumCommandLineLength;
        var command = truncated ? holder.CommandLine![..(MaximumCommandLineLength - 3)] + "..." : holder.CommandLine;
        return $"{Prefix} pid={holder.ProcessId} parent_pid={holder.ParentProcessId} parent_live={holder.ParentLive} " +
            $"image={Quote(holder.Image)} match={holder.Match} cwd={Quote(holder.CurrentDirectory)} " +
            $"cmd={Quote(command)} cmd_truncated={(truncated ? "true" : "false")}";
    }

    private static string Quote(string? value) => string.IsNullOrEmpty(value) ? "none"
        : "\"" + value.Replace('\r', ' ').Replace('\n', ' ').Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string NormalizeRoot(string root)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        }
        catch
        {
            return root;
        }
    }
}
