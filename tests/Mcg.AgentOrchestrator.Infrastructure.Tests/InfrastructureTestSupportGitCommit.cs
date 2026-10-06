using GitProbeResult = WorkerDispatchTestsSeededRepositoryFactory.GitProbeResult;

internal static partial class InfrastructureTestSupport
{
    internal static GitProbeResult RunGitWithCommitPostcondition(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        Func<string, IReadOnlyList<string>, GitProbeResult>? runGit = null)
    {
        runGit ??= static (directory, args) => RunGitProbe(directory, args);
        var isCommit = arguments.Any(argument => string.Equals(argument, "commit", StringComparison.Ordinal));
        string? previousHead = null;
        if (isCommit && !TryReadGitHeadInProcess(workingDirectory, out previousHead))
            previousHead = TryGetGitHead(workingDirectory);

        for (var attempt = 1; ; attempt++)
        {
            var result = runGit(workingDirectory, arguments);
            if (result.ExitCode is not int exitCode)
            {
                throw new InvalidOperationException(
                    $"git {string.Join(' ', arguments)} produced no exit code: " +
                    $"classification={result.Classification}; processStarted={result.ProcessStarted}; " +
                    $"timedOut={result.TimedOut}; drainTimedOut={result.DrainTimedOut}; " +
                    $"drainFailed={result.DrainFailed}; stdoutBytes={result.StandardOutputByteCount}; " +
                    $"stderrBytes={result.StandardErrorByteCount}; stderr={result.StandardError}");
            }

            if (exitCode == 0 || (isCommit && HasNewCommittedCleanGitHead(workingDirectory, previousHead)))
                return result;

            if (isCommit && attempt == 1 && string.IsNullOrWhiteSpace(result.StandardOutput) &&
                string.IsNullOrWhiteSpace(result.StandardError))
                continue;

            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed: exit={exitCode}{Environment.NewLine}" +
                $"stdout: {result.StandardOutput.Trim()}{Environment.NewLine}" +
                $"stderr: {result.StandardError.Trim()}");
        }
    }

    // true/null means unborn; false means unknown and requires the launched HEAD probe.
    internal static bool TryReadGitHeadInProcess(string workingDirectory, out string? head)
    {
        head = null;
        try
        {
            // Do not walk upward: fixtures may themselves be inside another repository.
            var repository = Path.GetFullPath(workingDirectory);
            var gitDirectory = Path.Combine(repository, ".git");
            if (!Directory.Exists(gitDirectory))
            {
                var pointer = File.ReadAllText(gitDirectory).Trim();
                if (!pointer.StartsWith("gitdir: ", StringComparison.Ordinal))
                    return false;

                var target = pointer[8..];
                if (string.IsNullOrWhiteSpace(target) || target.Contains('\n') || target.Contains('\r'))
                    return false;
                gitDirectory = Path.GetFullPath(target, repository);
            }

            var commonDirectory = gitDirectory;
            if (TryReadOptionalGitMetadata(Path.Combine(gitDirectory, "commondir"), out var common))
            {
                var target = common.Trim();
                if (string.IsNullOrWhiteSpace(target) || target.Contains('\n') || target.Contains('\r'))
                    return false;
                commonDirectory = Path.GetFullPath(target, gitDirectory);
            }

            if (!Directory.Exists(commonDirectory) ||
                Directory.Exists(Path.Combine(commonDirectory, "reftable")))
                return false;

            var value = File.ReadAllText(Path.Combine(gitDirectory, "HEAD")).Trim();
            if (IsGitObjectId(value))
            {
                head = value.ToLowerInvariant();
                return true;
            }

            if (!value.StartsWith("ref: refs/heads/", StringComparison.Ordinal))
                return false;

            var reference = value[5..];
            if (!IsReadableGitBranchReference(reference))
                return false;

            if (TryReadOptionalGitMetadata(Path.Combine(commonDirectory, reference), out var loose))
            {
                value = loose.Trim();
                if (!IsGitObjectId(value))
                    return false; // Includes unsupported symref chains, never guessed as unborn.
                head = value.ToLowerInvariant();
                return true;
            }

            if (TryReadOptionalGitMetadata(Path.Combine(commonDirectory, "packed-refs"), out var packed))
            {
                foreach (var line in packed.Split('\n'))
                {
                    var entry = line.Trim();
                    if (entry.Length == 0 || entry[0] is '#' or '^')
                        continue;

                    var separator = entry.IndexOf(' ');
                    if (separator < 0 || !IsGitObjectId(entry[..separator]))
                        return false;
                    if (string.Equals(entry[(separator + 1)..], reference, StringComparison.Ordinal))
                    {
                        head = entry[..separator].ToLowerInvariant();
                        return true;
                    }
                }
            }

            return true; // A valid branch HEAD without a loose or packed ref is unborn.
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                         ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryReadOptionalGitMetadata(string path, out string contents)
    {
        try
        {
            contents = File.ReadAllText(path);
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            contents = string.Empty;
            return false;
        }
    }

    private static bool IsGitObjectId(string value) =>
        value.Length is 40 or 64 && value.All(Uri.IsHexDigit);

    private static bool IsReadableGitBranchReference(string reference) =>
        !reference.Contains("..", StringComparison.Ordinal) &&
        !reference.Contains("@{", StringComparison.Ordinal) &&
        !reference.Any(character => char.IsControl(character) || char.IsWhiteSpace(character) ||
                                    "\\~^:?*[".Contains(character)) &&
        reference.Split('/').All(part => part.Length > 0 && !part.StartsWith('.') &&
                                        !part.EndsWith('.') && !part.EndsWith(".lock", StringComparison.Ordinal));
}
