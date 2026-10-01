using Mcg.AgentOrchestrator.Infrastructure;

internal static class AssemblyTempRootCleanupHolderDiagnostics
{
    internal static string Describe(string root, Func<ProcessCommandLineSnapshot> processSnapshot)
    {
        try
        {
            var normalizedRoot = NormalizeRoot(root).Replace('/', '\\');
            var snapshot = processSnapshot();
            var lines = snapshot.Records.Values
                .Where(process => process.Status is not (ProcessInspectionStatus.Exited or ProcessInspectionStatus.DeadOrRecycled))
                .Where(process => ContainsRoot(process.CommandLine) || ContainsRoot(process.ExecutablePath))
                .OrderBy(process => process.ProcessId)
                .Select(process => $"pid={process.ProcessId} parent={process.ParentProcessId} image={process.Name} cmd={process.CommandLine}")
                .ToList();
            if (lines.Count == 0)
            {
                lines.Add($"no live process command line or executable path contains '{root}'");
            }

            if (snapshot.Failure is { } failure)
            {
                lines.Add($"process snapshot incomplete: status={failure.Status}, operation={failure.Operation}, native_error={failure.NativeError}");
            }

            return string.Join(Environment.NewLine, lines);

            bool ContainsRoot(string? value) => value?.Replace('/', '\\')
                .Contains(normalizedRoot, StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (Exception exception)
        {
            return $"process snapshot unavailable: {exception.GetType().Name}: " +
                exception.Message.Replace('\r', ' ').Replace('\n', ' ');
        }
    }

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
