using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record HermesPinnedIdentity(string Release, string TagObject, string Commit)
{
    public static HermesPinnedIdentity Default { get; } = new(
        HermesAcpAdapter.PinnedRelease,
        HermesAcpAdapter.PinnedTagObject,
        HermesAcpAdapter.PinnedCommit);
}

internal sealed record HermesExecutableIdentityReceipt(
    string ImagePath,
    string InstallRoot,
    string ReportedInstallDirectory,
    string InstallMethod,
    string Release,
    string HeadCommit,
    string TagObject,
    string PeeledCommit,
    string ReportedVersionLine,
    bool WorkingTreeClean,
    DateTimeOffset VerifiedAtUtc,
    string VersionStandardOutput,
    string VersionStandardError,
    int VersionExitCode,
    bool VersionJobExitConfirmed);

internal enum HermesIdentityRefusal
{
    ImagePathUnavailable,
    ImagePathMissingOnDisk,
    NotGitCheckout,
    WrongCommit,
    TagMissing,
    TagNotAnnotated,
    TagObjectMismatch,
    PeeledCommitMismatch,
    WorkingTreeModified,
    WorkingTreeUnverifiable,
    InstallMethodNotGit,
    InstallDirectoryTextMissing,
    InstallDirectoryTextMismatch,
    ReleaseTokenMismatch,
    ReceiptMissing,
    ReceiptStale,
    ReceiptPinMismatch,
    ReceiptEvidenceInvalid,
    VersionProbeMissingExecutable,
    VersionProbeFailed,
    VersionProbeTimedOut,
    VersionProbeDrainTimedOut,
    VersionProbeUnresolvedChild
}

internal sealed class HermesIdentityException : InvalidOperationException
{
    public HermesIdentityException(HermesIdentityRefusal reason, string message, Exception? innerException = null)
        : base($"Hermes identity preflight refused ({reason}): {message}", innerException)
    {
        Reason = reason;
    }

    public HermesIdentityRefusal Reason { get; }
}

internal interface IHermesExecutableIdentityVerifier
{
    HermesPinnedIdentity Pin { get; }

    Task<HermesExecutableIdentityReceipt> VerifyAsync(
        string? launchedImagePath,
        string versionStandardOutput,
        string versionStandardError,
        int versionExitCode,
        bool versionJobExitConfirmed,
        CancellationToken cancellationToken);
}

internal sealed class GitHermesExecutableIdentityVerifier : IHermesExecutableIdentityVerifier
{
    internal static readonly TimeSpan MaximumReceiptAge = TimeSpan.FromMinutes(1);
    private const int GitTimeoutMilliseconds = 5_000;
    private static readonly Regex ReleaseToken = new(@"\((?<release>\d{4}\.\d{1,2}\.\d{1,2})\)", RegexOptions.CultureInvariant);
    private readonly HermesPinnedIdentity _pin;

    public GitHermesExecutableIdentityVerifier(HermesPinnedIdentity? pin = null)
    {
        _pin = pin ?? HermesPinnedIdentity.Default;
    }

    public HermesPinnedIdentity Pin => _pin;

    public Task<HermesExecutableIdentityReceipt> VerifyAsync(
        string? launchedImagePath,
        string versionStandardOutput,
        string versionStandardError,
        int versionExitCode,
        bool versionJobExitConfirmed,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Verify(
            launchedImagePath,
            versionStandardOutput,
            versionStandardError,
            versionExitCode,
            versionJobExitConfirmed,
            cancellationToken));
    }

    internal static void ValidateReceipt(
        HermesExecutableIdentityReceipt? receipt,
        DateTimeOffset? now = null,
        HermesPinnedIdentity? pin = null,
        bool enforceFreshness = true)
    {
        if (receipt is null)
            throw Refuse(HermesIdentityRefusal.ReceiptMissing, "terminal evidence contains no executable identity receipt");

        var expected = pin ?? HermesPinnedIdentity.Default;
        if (enforceFreshness)
        {
            var observedAt = now ?? DateTimeOffset.UtcNow;
            var age = observedAt - receipt.VerifiedAtUtc;
            if (age < TimeSpan.Zero || age > MaximumReceiptAge)
                throw Refuse(HermesIdentityRefusal.ReceiptStale, $"receipt age {age.TotalSeconds:F1}s is outside the 0-{MaximumReceiptAge.TotalSeconds:F0}s window");
        }

        if (!string.Equals(receipt.Release, expected.Release, StringComparison.OrdinalIgnoreCase) ||
            !EqualsSha(receipt.HeadCommit, expected.Commit) ||
            !EqualsSha(receipt.TagObject, expected.TagObject) ||
            !EqualsSha(receipt.PeeledCommit, expected.Commit))
        {
            throw Refuse(HermesIdentityRefusal.ReceiptPinMismatch, "receipt does not match the configured release, tag object, and peeled commit pin");
        }

        var pathsMatch = false;
        try
        {
            pathsMatch = !string.IsNullOrWhiteSpace(receipt.ImagePath) &&
                !string.IsNullOrWhiteSpace(receipt.InstallRoot) &&
                !string.IsNullOrWhiteSpace(receipt.ReportedInstallDirectory) &&
                PathEquals(NormalizePath(receipt.InstallRoot), NormalizePath(receipt.ReportedInstallDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Refuse(HermesIdentityRefusal.ReceiptEvidenceInvalid, $"receipt contains invalid path evidence: {ex.Message}");
        }

        if (!receipt.WorkingTreeClean || receipt.VersionExitCode != 0 || !receipt.VersionJobExitConfirmed ||
            !string.Equals(receipt.InstallMethod, "git", StringComparison.OrdinalIgnoreCase) || !pathsMatch)
        {
            throw Refuse(
                HermesIdentityRefusal.ReceiptEvidenceInvalid,
                "receipt does not prove a clean Git checkout, matching derived/reported installation, and exited version child");
        }
    }

    private HermesExecutableIdentityReceipt Verify(
        string? launchedImagePath,
        string versionStandardOutput,
        string versionStandardError,
        int versionExitCode,
        bool versionJobExitConfirmed,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(launchedImagePath))
            throw Refuse(HermesIdentityRefusal.ImagePathUnavailable, "the operating system did not report the launched image path");

        string imagePath;
        try
        {
            imagePath = Path.GetFullPath(launchedImagePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Refuse(HermesIdentityRefusal.ImagePathMissingOnDisk, $"launched image path is invalid: {ex.Message}");
        }
        if (!File.Exists(imagePath))
            throw Refuse(HermesIdentityRefusal.ImagePathMissingOnDisk, $"launched image does not exist: '{imagePath}'");

        var imageDirectory = Path.GetDirectoryName(imagePath)!;
        var topLevel = RunGit(imageDirectory, cancellationToken, "rev-parse", "--show-toplevel");
        if (!topLevel.Succeeded || topLevel.DrainTimedOut || string.IsNullOrWhiteSpace(topLevel.Output))
            throw Refuse(HermesIdentityRefusal.NotGitCheckout, GitFailure("could not derive an enclosing checkout from the launched image", topLevel));

        var installRoot = NormalizePath(topLevel.Output.Trim());
        var head = RequireGitOutput(installRoot, HermesIdentityRefusal.WrongCommit, cancellationToken, "rev-parse", "HEAD");
        if (!EqualsSha(head, _pin.Commit))
            throw Refuse(HermesIdentityRefusal.WrongCommit, $"expected HEAD {_pin.Commit}, observed {head}");

        var tagRef = $"refs/tags/{_pin.Release}";
        var tagTypeResult = RunGit(installRoot, cancellationToken, "cat-file", "-t", tagRef);
        if (!tagTypeResult.Succeeded || tagTypeResult.DrainTimedOut)
            throw Refuse(HermesIdentityRefusal.TagMissing, GitFailure($"annotated tag '{_pin.Release}' is unavailable", tagTypeResult));
        if (!tagTypeResult.Output.Trim().Equals("tag", StringComparison.Ordinal))
            throw Refuse(HermesIdentityRefusal.TagNotAnnotated, $"'{_pin.Release}' resolves to {tagTypeResult.Output.Trim()}, not an annotated tag object");

        var tagObject = RequireGitOutput(installRoot, HermesIdentityRefusal.TagMissing, cancellationToken, "rev-parse", tagRef);
        if (!EqualsSha(tagObject, _pin.TagObject))
            throw Refuse(HermesIdentityRefusal.TagObjectMismatch, $"expected tag object {_pin.TagObject}, observed {tagObject}");

        var peeledCommit = RequireGitOutput(installRoot, HermesIdentityRefusal.PeeledCommitMismatch, cancellationToken, "rev-parse", $"{tagRef}^{{}}");
        if (!EqualsSha(peeledCommit, _pin.Commit))
            throw Refuse(HermesIdentityRefusal.PeeledCommitMismatch, $"expected peeled commit {_pin.Commit}, observed {peeledCommit}");

        var status = RunGit(installRoot, cancellationToken, "status", "--porcelain=v1", "--untracked-files=no");
        if (!status.Succeeded || status.DrainTimedOut)
            throw Refuse(HermesIdentityRefusal.WorkingTreeUnverifiable, GitFailure("tracked working-tree state could not be verified", status));
        if (!string.IsNullOrWhiteSpace(status.Output))
            throw Refuse(HermesIdentityRefusal.WorkingTreeModified, $"tracked working tree is modified: {status.Output.Trim()}");

        var lines = versionStandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var versionLine = lines.FirstOrDefault() ?? string.Empty;
        var match = ReleaseToken.Match(versionLine);
        var expectedReleaseToken = _pin.Release.TrimStart('v', 'V');
        if (!match.Success || !match.Groups["release"].Value.Equals(expectedReleaseToken, StringComparison.OrdinalIgnoreCase))
            throw Refuse(HermesIdentityRefusal.ReleaseTokenMismatch, $"expected native release token '({expectedReleaseToken})', observed '{versionLine}'");

        var installMethod = ValueAfterPrefix(lines, "Install method:");
        if (!string.Equals(installMethod, "git", StringComparison.OrdinalIgnoreCase))
            throw Refuse(HermesIdentityRefusal.InstallMethodNotGit, $"expected 'Install method: git', observed '{installMethod ?? "<missing>"}'");

        var reportedInstallDirectory = ValueAfterPrefix(lines, "Install directory:");
        if (string.IsNullOrWhiteSpace(reportedInstallDirectory))
            throw Refuse(HermesIdentityRefusal.InstallDirectoryTextMissing, "native version output did not report an install directory");
        string normalizedReportedDirectory;
        try
        {
            normalizedReportedDirectory = NormalizePath(reportedInstallDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Refuse(HermesIdentityRefusal.InstallDirectoryTextMismatch, $"reported install directory is invalid: {ex.Message}");
        }

        if (!PathEquals(normalizedReportedDirectory, installRoot))
            throw Refuse(HermesIdentityRefusal.InstallDirectoryTextMismatch, $"OS-derived checkout '{installRoot}' does not match reported install directory '{normalizedReportedDirectory}'");

        return new HermesExecutableIdentityReceipt(
            imagePath,
            installRoot,
            normalizedReportedDirectory,
            installMethod!,
            _pin.Release,
            head,
            tagObject,
            peeledCommit,
            versionLine,
            WorkingTreeClean: true,
            DateTimeOffset.UtcNow,
            versionStandardOutput,
            versionStandardError,
            versionExitCode,
            versionJobExitConfirmed);
    }

    private static GitCli.GitResult RunGit(string workingDirectory, CancellationToken cancellationToken, params string[] arguments)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return GitCli.Run(workingDirectory, GitTimeoutMilliseconds, arguments);
    }

    private static string RequireGitOutput(
        string workingDirectory,
        HermesIdentityRefusal reason,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        var result = RunGit(workingDirectory, cancellationToken, arguments);
        if (!result.Succeeded || result.DrainTimedOut || string.IsNullOrWhiteSpace(result.Output))
            throw Refuse(reason, GitFailure($"git {string.Join(' ', arguments)} did not return identity evidence", result));
        return result.Output.Trim();
    }

    private static string? ValueAfterPrefix(IEnumerable<string> lines, string prefix)
    {
        var line = lines.FirstOrDefault(candidate => candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return line is null ? null : line[prefix.Length..].Trim();
    }

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));

    private static bool PathEquals(string left, string right) =>
        left.Equals(right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool EqualsSha(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string GitFailure(string context, GitCli.GitResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.Error) ? result.Output.Trim() : result.Error.Trim();
        return $"{context}; exit={result.ExitCode}; started={result.ProcessStarted}; drainTimedOut={result.DrainTimedOut}; detail='{detail}'";
    }

    private static HermesIdentityException Refuse(HermesIdentityRefusal reason, string message) => new(reason, message);
}
