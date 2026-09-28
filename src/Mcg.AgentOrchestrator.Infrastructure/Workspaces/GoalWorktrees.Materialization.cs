using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static partial class GoalWorktrees
{
    private const int MaximumRematerializationPaths = 25;

    private sealed record MaterializationIdentity(string Head, string BranchHead, string TopLevel);

    private sealed record MaterializationCandidate(
        string RelativePath,
        string FullPath,
        string CommittedBlob,
        string WorkingBlob,
        string IndexMode,
        byte[] WorkingBytes);

    internal static GoalWorktreeRebaseResult ValidatePostRebaseMaterialization(
        string worktreePath,
        string branch,
        string baseBranch,
        GoalId goalId,
        Func<string, string[], GitCli.GitResult>? gitRunner = null)
    {
        gitRunner ??= static (workingDirectory, args) => GitCli.Run(workingDirectory, args);

        if (!TryReadStatus(worktreePath, gitRunner, out var statusOutput, out var failure))
        {
            return Incomplete(branch, goalId, $"post-rebase status could not be verified: {failure}");
        }

        if (statusOutput.Length == 0)
        {
            return Rebased(branch, baseBranch, goalId, [], null);
        }

        if (!TryCaptureIdentity(worktreePath, branch, gitRunner, out var identity, out failure))
        {
            return Incomplete(branch, goalId, $"post-rebase identity could not be verified: {failure}");
        }

        if (!TryEnsureNoGitOperation(worktreePath, gitRunner, out failure))
        {
            return Incomplete(branch, goalId, failure);
        }

        if (!TryReadSingleLine(worktreePath, gitRunner, "object format", ["rev-parse", "--show-object-format"], out var objectFormat, out failure) ||
            (objectFormat != "sha1" && objectFormat != "sha256"))
        {
            return Incomplete(
                branch,
                goalId,
                string.IsNullOrEmpty(failure)
                    ? $"unsupported Git object format '{objectFormat}'; only sha1 and sha256 can be verified"
                    : failure);
        }

        if (!TryParseDirtyPaths(statusOutput, out var dirtyPaths, out failure))
        {
            return Incomplete(branch, goalId, failure);
        }

        if (dirtyPaths.Count > MaximumRematerializationPaths)
        {
            return Incomplete(
                branch,
                goalId,
                $"post-rebase checkout has {dirtyPaths.Count} dirty paths, exceeding the guarded {MaximumRematerializationPaths}-path rematerialization limit");
        }

        var candidates = new List<MaterializationCandidate>(dirtyPaths.Count);
        foreach (var relativePath in dirtyPaths)
        {
            if (!TryClassifyCandidate(
                    worktreePath,
                    relativePath,
                    objectFormat,
                    gitRunner,
                    out var candidate,
                    out failure))
            {
                return Incomplete(branch, goalId, failure);
            }

            candidates.Add(candidate);
        }

        if (!TryVerifyUnchangedBeforeWrite(worktreePath, branch, statusOutput, identity, candidates, gitRunner, out failure))
        {
            return Incomplete(branch, goalId, failure);
        }

        var preimageDirectory = ResolvePreimageDirectory(worktreePath, gitRunner, out failure);
        if (preimageDirectory is null)
        {
            return Incomplete(branch, goalId, failure);
        }

        try
        {
            Directory.CreateDirectory(preimageDirectory);
            foreach (var candidate in candidates)
            {
                var receiptName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(candidate.RelativePath)))
                    .ToLowerInvariant() + ".bin";
                File.WriteAllBytes(Path.Combine(preimageDirectory, receiptName), candidate.WorkingBytes);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Incomplete(
                branch,
                goalId,
                $"could not preserve byte-exact preimages before rematerialization: {ex.Message}",
                preimageDirectory: preimageDirectory);
        }

        var rematerialized = new List<string>(candidates.Count);
        var attempts = new List<RematerializationAttempt>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (!TryInvalidateCachedStat(candidate, out failure))
            {
                return Incomplete(
                    branch,
                    goalId,
                    $"could not invalidate cached stat data for '{candidate.RelativePath}' before guarded checkout; checkout was not attempted: {failure}; exact preimages remain at '{preimageDirectory}'",
                    rematerialized,
                    preimageDirectory);
            }

            var before = CaptureMaterializationSnapshot(candidate.FullPath);
            var checkout = gitRunner(worktreePath, ["checkout", "--", candidate.RelativePath]);
            var after = CaptureMaterializationSnapshot(candidate.FullPath);
            attempts.Add(new RematerializationAttempt(candidate.RelativePath, before, after, checkout));
            if (!checkout.Succeeded || checkout.DrainTimedOut)
            {
                var attempted = rematerialized.Append(candidate.RelativePath).ToArray();
                return Incomplete(
                    branch,
                    goalId,
                    $"guarded checkout was attempted for '{candidate.RelativePath}' and may have modified it, but completion could not be verified: {DescribeGitFailure(checkout)}; exact preimages remain at '{preimageDirectory}'",
                    attempted,
                    preimageDirectory);
            }

            rematerialized.Add(candidate.RelativePath);
        }

        if (!TryVerifyAfterWrite(worktreePath, branch, identity, candidates, gitRunner, out failure, out var failureKind, out var dirtyStatus))
        {
            var evidence = failureKind is AfterWriteFailureKind.StillDirty or AfterWriteFailureKind.BytesMismatch
                ? DescribeRematerializationEvidence(worktreePath, attempts, dirtyStatus, gitRunner)
                : string.Empty;
            return Incomplete(
                branch,
                goalId,
                $"guarded rematerialization did not produce a verified usable checkout: {failure}; exact preimages remain at '{preimageDirectory}'{evidence}",
                rematerialized,
                preimageDirectory);
        }

        return Rebased(branch, baseBranch, goalId, rematerialized, preimageDirectory);
    }

    private static bool TryParseDirtyPaths(string statusOutput, out IReadOnlyList<string> paths, out string failure)
    {
        var parsed = new List<string>();
        foreach (var entry in statusOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (entry.Length < 4 || !entry.StartsWith(" M ", StringComparison.Ordinal))
            {
                paths = [];
                failure = $"post-rebase status contains an unowned or semantic change ('{CollapseControlWhitespace(entry)}'); only clean-index worktree modifications are eligible";
                return false;
            }

            var path = entry[3..];
            if (string.IsNullOrWhiteSpace(path) || parsed.Contains(path, StringComparer.Ordinal))
            {
                paths = [];
                failure = "post-rebase status contains an empty or duplicate path";
                return false;
            }

            parsed.Add(path);
        }

        paths = parsed;
        if (parsed.Count == 0)
        {
            failure = "post-rebase status contained no commit-worthy paths after parsing";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    private static bool TryClassifyCandidate(
        string worktreePath,
        string relativePath,
        string objectFormat,
        Func<string, string[], GitCli.GitResult> gitRunner,
        out MaterializationCandidate candidate,
        out string failure)
    {
        candidate = null!;
        var root = NormalizeMaterializationPath(worktreePath);
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (Path.IsPathRooted(relativePath) ||
            !fullPath.StartsWith(root + Path.DirectorySeparatorChar, comparison) ||
            !File.Exists(fullPath))
        {
            failure = $"dirty path '{relativePath}' is not an owned regular file inside the resolved worktree";
            return false;
        }

        if (!TryReadSingleLine(worktreePath, gitRunner, $"index entry for '{relativePath}'", ["ls-files", "-s", "-z", "--", relativePath], out var indexEntry, out failure) ||
            !TryParseIndexEntry(indexEntry, relativePath, out var indexMode, out var indexBlob))
        {
            if (string.IsNullOrEmpty(failure))
                failure = $"dirty path '{relativePath}' has no single stage-0 index entry";
            return false;
        }

        if (indexMode != "100644" && indexMode != "100755")
        {
            failure = $"dirty path '{relativePath}' has unsupported index mode {indexMode}; symlinks, gitlinks, and special entries are never rematerialized";
            return false;
        }

        if (!TryReadSingleLine(worktreePath, gitRunner, $"committed blob for '{relativePath}'", ["rev-parse", $"HEAD:{relativePath.Replace('\\', '/')}"], out var committedBlob, out failure) ||
            !string.Equals(indexBlob, committedBlob, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(failure))
                failure = $"index/blob identity drifted for '{relativePath}'";
            return false;
        }

        if (!TryReadAttributes(worktreePath, relativePath, gitRunner, out var attributes, out failure))
        {
            return false;
        }

        if (attributes["text"] != "unset" ||
            attributes["binary"] != "unspecified" ||
            attributes["filter"] != "unspecified" ||
            attributes["diff"] != "unspecified")
        {
            failure = $"dirty path '{relativePath}' is not eligible: expected effective -text with no binary, filter, or diff driver; got text={attributes["text"]}, binary={attributes["binary"]}, filter={attributes["filter"]}, diff={attributes["diff"]}";
            return false;
        }

        if (!TryReadSingleLine(worktreePath, gitRunner, $"working blob for '{relativePath}'", ["hash-object", "--no-filters", "--", relativePath], out var workingBlob, out failure) ||
            string.Equals(workingBlob, committedBlob, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(failure))
                failure = $"dirty path '{relativePath}' does not carry the known working-byte conversion signature";
            return false;
        }

        byte[] workingBytes;
        try
        {
            workingBytes = File.ReadAllBytes(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = $"working bytes for '{relativePath}' could not be read: {ex.Message}";
            return false;
        }

        if (!TryNormalizeCrLfOnly(workingBytes, out var normalizedBytes))
        {
            failure = $"dirty path '{relativePath}' contains no CRLF conversion or contains a bare CR; mixed/CR-bearing bytes are never rematerialized";
            return false;
        }

        var normalizedBlob = ComputeGitBlobId(normalizedBytes, objectFormat);
        if (!string.Equals(normalizedBlob, committedBlob, StringComparison.OrdinalIgnoreCase))
        {
            failure = $"dirty path '{relativePath}' differs from the committed blob by more than CRLF conversion";
            return false;
        }

        candidate = new MaterializationCandidate(relativePath, fullPath, committedBlob, workingBlob, indexMode, workingBytes);
        failure = string.Empty;
        return true;
    }

    private static bool TryVerifyUnchangedBeforeWrite(
        string worktreePath,
        string branch,
        string originalStatus,
        MaterializationIdentity identity,
        IReadOnlyList<MaterializationCandidate> candidates,
        Func<string, string[], GitCli.GitResult> gitRunner,
        out string failure)
    {
        if (!TryCaptureIdentity(worktreePath, branch, gitRunner, out var currentIdentity, out failure) || currentIdentity != identity)
        {
            failure = failure.Length > 0 ? failure : "repository identity drifted during materialization classification";
            return false;
        }

        if (!TryEnsureNoGitOperation(worktreePath, gitRunner, out failure) ||
            !TryReadStatus(worktreePath, gitRunner, out var currentStatus, out failure) ||
            !string.Equals(originalStatus, currentStatus, StringComparison.Ordinal))
        {
            failure = failure.Length > 0 ? failure : "worktree status drifted during materialization classification";
            return false;
        }

        foreach (var candidate in candidates)
        {
            if (!TryReadSingleLine(worktreePath, gitRunner, $"working blob for '{candidate.RelativePath}'", ["hash-object", "--no-filters", "--", candidate.RelativePath], out var workingBlob, out failure) ||
                !string.Equals(workingBlob, candidate.WorkingBlob, StringComparison.OrdinalIgnoreCase))
            {
                failure = failure.Length > 0 ? failure : $"working bytes for '{candidate.RelativePath}' drifted before rematerialization";
                return false;
            }
        }

        failure = string.Empty;
        return true;
    }

    private static bool TryVerifyAfterWrite(
        string worktreePath,
        string branch,
        MaterializationIdentity identity,
        IReadOnlyList<MaterializationCandidate> candidates,
        Func<string, string[], GitCli.GitResult> gitRunner,
        out string failure,
        out AfterWriteFailureKind failureKind,
        out string dirtyStatus)
    {
        failureKind = AfterWriteFailureKind.Other;
        dirtyStatus = string.Empty;
        if (!TryCaptureIdentity(worktreePath, branch, gitRunner, out var currentIdentity, out failure) || currentIdentity != identity)
        {
            failure = failure.Length > 0 ? failure : "repository identity changed during rematerialization";
            return false;
        }

        var status = string.Empty;
        if (!TryEnsureNoGitOperation(worktreePath, gitRunner, out failure) ||
            !TryReadStatus(worktreePath, gitRunner, out status, out failure) ||
            status.Length > 0)
        {
            if (failure.Length == 0 && status.Length > 0)
            {
                failureKind = AfterWriteFailureKind.StillDirty;
                dirtyStatus = status;
            }
            failure = failure.Length > 0 ? failure : "worktree is still dirty after rematerialization";
            return false;
        }

        foreach (var candidate in candidates)
        {
            var blobRead = TryReadSingleLine(worktreePath, gitRunner, $"rematerialized blob for '{candidate.RelativePath}'", ["hash-object", "--no-filters", "--", candidate.RelativePath], out var workingBlob, out failure);
            if (!blobRead ||
                !string.Equals(workingBlob, candidate.CommittedBlob, StringComparison.OrdinalIgnoreCase))
            {
                if (blobRead && failure.Length == 0)
                {
                    failureKind = AfterWriteFailureKind.BytesMismatch;
                }
                failure = failure.Length > 0 ? failure : $"rematerialized bytes for '{candidate.RelativePath}' do not match HEAD";
                return false;
            }

            if (!TryReadSingleLine(worktreePath, gitRunner, $"index entry for '{candidate.RelativePath}'", ["ls-files", "-s", "-z", "--", candidate.RelativePath], out var indexEntry, out failure) ||
                !TryParseIndexEntry(indexEntry, candidate.RelativePath, out var mode, out var blob) ||
                mode != candidate.IndexMode ||
                !string.Equals(blob, candidate.CommittedBlob, StringComparison.OrdinalIgnoreCase))
            {
                failure = failure.Length > 0 ? failure : $"index mode/blob identity changed for '{candidate.RelativePath}'";
                return false;
            }
        }

        failure = string.Empty;
        return true;
    }

    private static bool TryCaptureIdentity(
        string worktreePath,
        string branch,
        Func<string, string[], GitCli.GitResult> gitRunner,
        out MaterializationIdentity identity,
        out string failure)
    {
        identity = null!;
        if (!TryReadSingleLine(worktreePath, gitRunner, "HEAD identity", ["rev-parse", "HEAD"], out var head, out failure) ||
            !TryReadSingleLine(worktreePath, gitRunner, "branch identity", ["rev-parse", $"refs/heads/{branch}"], out var branchHead, out failure) ||
            !TryReadSingleLine(worktreePath, gitRunner, "worktree root", ["rev-parse", "--show-toplevel"], out var topLevel, out failure))
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(head, branchHead, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(NormalizeMaterializationPath(worktreePath), NormalizeMaterializationPath(topLevel), comparison))
        {
            failure = "HEAD, goal-branch, or resolved-worktree identity does not match after rebase";
            return false;
        }

        identity = new MaterializationIdentity(head, branchHead, NormalizeMaterializationPath(topLevel));
        failure = string.Empty;
        return true;
    }

    private static bool TryEnsureNoGitOperation(
        string worktreePath,
        Func<string, string[], GitCli.GitResult> gitRunner,
        out string failure)
    {
        foreach (var marker in new[] { "rebase-merge", "rebase-apply", "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD" })
        {
            if (!TryReadSingleLine(worktreePath, gitRunner, $"Git operation marker '{marker}'", ["rev-parse", "--git-path", marker], out var markerPath, out failure))
            {
                return false;
            }

            var resolvedMarker = Path.IsPathRooted(markerPath)
                ? Path.GetFullPath(markerPath)
                : Path.GetFullPath(Path.Combine(worktreePath, markerPath));
            if (File.Exists(resolvedMarker) || Directory.Exists(resolvedMarker))
            {
                failure = $"active Git operation marker '{marker}' prevents post-rebase rematerialization";
                return false;
            }
        }

        failure = string.Empty;
        return true;
    }

    private static bool TryReadAttributes(
        string worktreePath,
        string relativePath,
        Func<string, string[], GitCli.GitResult> gitRunner,
        out IReadOnlyDictionary<string, string> attributes,
        out string failure)
    {
        var result = gitRunner(worktreePath, ["check-attr", "-z", "text", "binary", "filter", "diff", "--", relativePath]);
        if (!result.Succeeded || result.DrainTimedOut)
        {
            attributes = new Dictionary<string, string>();
            failure = $"attributes for '{relativePath}' could not be verified: {DescribeGitFailure(result)}";
            return false;
        }

        var parts = result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        if (parts.Length != 12)
        {
            attributes = parsed;
            failure = $"attributes for '{relativePath}' returned a malformed receipt";
            return false;
        }

        for (var index = 0; index < parts.Length; index += 3)
        {
            if (!string.Equals(parts[index], relativePath, StringComparison.Ordinal))
            {
                attributes = parsed;
                failure = $"attributes for '{relativePath}' returned a different path";
                return false;
            }

            parsed[parts[index + 1]] = parts[index + 2];
        }

        if (!new[] { "text", "binary", "filter", "diff" }.All(parsed.ContainsKey))
        {
            attributes = parsed;
            failure = $"attributes for '{relativePath}' omitted a required key";
            return false;
        }

        attributes = parsed;
        failure = string.Empty;
        return true;
    }

    private static bool TryReadStatus(
        string worktreePath,
        Func<string, string[], GitCli.GitResult> gitRunner,
        out string output,
        out string failure)
    {
        var result = gitRunner(worktreePath, ["status", "--porcelain=v1", "-z", "--untracked-files=all"]);
        if (!result.Succeeded || result.DrainTimedOut)
        {
            output = string.Empty;
            failure = DescribeGitFailure(result);
            return false;
        }

        var statusOutput = FilterInternalArtifactStatusEntries(result.Output);
        var eolResult = gitRunner(worktreePath, ["ls-files", "--eol", "-z"]);
        if (!eolResult.Succeeded || eolResult.DrainTimedOut)
        {
            output = string.Empty;
            failure = $"byte-preserving tracked-file scan could not be verified: {DescribeGitFailure(eolResult)}";
            return false;
        }

        if (!TryParseImplicitConversionPaths(eolResult.Output, out var implicitPaths, out failure))
        {
            output = string.Empty;
            return false;
        }

        var entries = statusOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        var explicitPaths = entries
            .Where(entry => entry.StartsWith(" M ", StringComparison.Ordinal) && entry.Length > 3)
            .Select(entry => entry[3..])
            .ToHashSet(StringComparer.Ordinal);
        foreach (var path in implicitPaths)
        {
            if (explicitPaths.Add(path))
            {
                entries.Add($" M {path}");
            }
        }

        output = entries.Count == 0 ? string.Empty : string.Join('\0', entries) + '\0';
        failure = string.Empty;
        return true;
    }

    private static bool TryParseImplicitConversionPaths(
        string eolOutput,
        out IReadOnlyList<string> paths,
        out string failure)
    {
        var parsed = new List<string>();
        foreach (var entry in eolOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = entry.IndexOf('\t');
            if (separator <= 0 || separator == entry.Length - 1)
            {
                paths = [];
                failure = "byte-preserving tracked-file scan returned a malformed receipt";
                return false;
            }

            var metadata = entry[..separator];
            var path = entry[(separator + 1)..];
            if (!TryParseEolMetadata(metadata, out var indexEol, out var worktreeEol, out var attributes) ||
                string.IsNullOrWhiteSpace(path))
            {
                paths = [];
                failure = "byte-preserving tracked-file scan returned a malformed receipt";
                return false;
            }

            if (indexEol == "i/lf" &&
                worktreeEol == "w/crlf" &&
                attributes == "attr/-text" &&
                !GitCli.IsOrchestratorInternalArtifactPath(path))
            {
                parsed.Add(path);
            }
        }

        paths = parsed;
        failure = string.Empty;
        return true;
    }

    private static bool TryParseEolMetadata(
        string metadata,
        out string indexEol,
        out string worktreeEol,
        out string attributes)
    {
        indexEol = string.Empty;
        worktreeEol = string.Empty;
        attributes = string.Empty;

        var indexEnd = metadata.IndexOf(' ');
        if (indexEnd <= 0)
            return false;

        var worktreeStart = indexEnd;
        while (worktreeStart < metadata.Length && metadata[worktreeStart] == ' ')
            worktreeStart++;

        var worktreeEnd = metadata.IndexOf(' ', worktreeStart);
        if (worktreeStart == metadata.Length || worktreeEnd <= worktreeStart)
            return false;

        var attributesStart = worktreeEnd;
        while (attributesStart < metadata.Length && metadata[attributesStart] == ' ')
            attributesStart++;

        if (attributesStart == metadata.Length)
            return false;

        indexEol = metadata[..indexEnd];
        worktreeEol = metadata[worktreeStart..worktreeEnd];
        attributes = metadata[attributesStart..].TrimEnd(' ');
        return indexEol.StartsWith("i/", StringComparison.Ordinal) &&
               worktreeEol.StartsWith("w/", StringComparison.Ordinal) &&
               attributes.StartsWith("attr/", StringComparison.Ordinal);
    }

    private static string FilterInternalArtifactStatusEntries(string statusOutput)
    {
        if (statusOutput.Length == 0)
        {
            return statusOutput;
        }

        var retained = statusOutput
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(entry => entry.Length < 4 || !GitCli.IsOrchestratorInternalArtifactPath(entry[3..]))
            .ToArray();
        return retained.Length == 0 ? string.Empty : string.Join('\0', retained) + '\0';
    }

    private static bool TryReadSingleLine(
        string worktreePath,
        Func<string, string[], GitCli.GitResult> gitRunner,
        string operation,
        string[] arguments,
        out string output,
        out string failure)
    {
        var result = gitRunner(worktreePath, arguments);
        output = result.Output.Trim().TrimEnd('\0');
        if (!result.Succeeded || result.DrainTimedOut)
        {
            failure = $"{operation} failed: {DescribeGitFailure(result)}";
            return false;
        }

        if (output.Length == 0 || output.Contains('\r') || output.Contains('\n') || output.Contains('\0'))
        {
            failure = $"{operation} returned an empty or malformed single-line receipt";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    private static string? ResolvePreimageDirectory(
        string worktreePath,
        Func<string, string[], GitCli.GitResult> gitRunner,
        out string failure)
    {
        if (!TryReadSingleLine(worktreePath, gitRunner, "preimage Git path", ["rev-parse", "--git-path", "mcg-rematerialization"], out var gitPath, out failure))
        {
            return null;
        }

        var root = Path.IsPathRooted(gitPath)
            ? Path.GetFullPath(gitPath)
            : Path.GetFullPath(Path.Combine(worktreePath, gitPath));
        failure = string.Empty;
        return Path.Combine(root, DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static bool TryParseIndexEntry(string entry, string expectedPath, out string mode, out string blob)
    {
        mode = string.Empty;
        blob = string.Empty;
        var tab = entry.IndexOf('\t');
        if (tab < 0 || !string.Equals(entry[(tab + 1)..], expectedPath, StringComparison.Ordinal))
        {
            return false;
        }

        var fields = entry[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 3 || fields[2] != "0")
        {
            return false;
        }

        mode = fields[0];
        blob = fields[1];
        return true;
    }

    private static bool TryNormalizeCrLfOnly(byte[] bytes, out byte[] normalized)
    {
        var output = new List<byte>(bytes.Length);
        var sawCrLf = false;
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] != (byte)'\r')
            {
                output.Add(bytes[index]);
                continue;
            }

            if (index + 1 >= bytes.Length || bytes[index + 1] != (byte)'\n')
            {
                normalized = [];
                return false;
            }

            sawCrLf = true;
            output.Add((byte)'\n');
            index++;
        }

        normalized = output.ToArray();
        return sawCrLf;
    }

    private static string ComputeGitBlobId(byte[] content, string objectFormat)
    {
        var header = Encoding.ASCII.GetBytes($"blob {content.Length}\0");
        var input = new byte[header.Length + content.Length];
        Buffer.BlockCopy(header, 0, input, 0, header.Length);
        Buffer.BlockCopy(content, 0, input, header.Length, content.Length);
        var hash = objectFormat == "sha256" ? SHA256.HashData(input) : SHA1.HashData(input);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string NormalizeMaterializationPath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string DescribeGitFailure(GitCli.GitResult result)
    {
        var drain = result.DrainTimedOut ? "; output drain timed out" : string.Empty;
        var stderr = CollapseControlWhitespace(result.Error);
        var stdout = CollapseControlWhitespace(result.Output);
        return $"exit={result.ExitCode}{drain}; stderr={(stderr.Length == 0 ? "<empty>" : stderr)}; stdout={(stdout.Length == 0 ? "<empty>" : stdout)}";
    }

    private static GoalWorktreeRebaseResult Rebased(
        string branch,
        string baseBranch,
        GoalId goalId,
        IReadOnlyList<string> rematerialized,
        string? preimageDirectory)
    {
        var receipt = rematerialized.Count == 0
            ? $"Rebased {branch} onto {baseBranch}; verified clean byte-exact materialization."
            : $"Rebased {branch} onto {baseBranch}; GoalWorktrees rematerialized {rematerialized.Count} byte-exact path(s) ({string.Join(", ", rematerialized)}) after proving CRLF-only conversion. Exact preimages: {preimageDirectory}.";
        return new GoalWorktreeRebaseResult(
            GoalWorktreeRebaseStatus.Rebased,
            branch,
            receipt + " Acceptance can now fast-forward after review.",
            [],
            $"acceptance {Prefix(goalId)}",
            rematerialized,
            preimageDirectory);
    }

    private static GoalWorktreeRebaseResult Incomplete(
        string branch,
        GoalId goalId,
        string reason,
        IReadOnlyList<string>? rematerialized = null,
        string? preimageDirectory = null) =>
        new(
            GoalWorktreeRebaseStatus.IncompleteMaterialization,
            branch,
            $"Rebase completed, but checkout materialization is incomplete and integration readiness is refused: {reason}",
            [],
            $"Inspect the goal worktree and any preserved preimages, then run workspace rebase {Prefix(goalId)} again after restoring a clean owned state.",
            rematerialized,
            preimageDirectory);
}
