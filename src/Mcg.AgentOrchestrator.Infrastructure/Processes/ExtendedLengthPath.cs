namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class ExtendedLengthPath
{
    internal static string Convert(string path) => Convert(path, OperatingSystem.IsWindows());

    internal static string Convert(string path, bool isWindows)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path;
        }

        var fullPath = Path.GetFullPath(path);
        if (!isWindows)
        {
            return fullPath;
        }

        return fullPath.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + fullPath[2..]
            : @"\\?\" + fullPath;
    }
}
