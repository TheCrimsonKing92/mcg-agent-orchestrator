using System.Security.Cryptography;
using System.Text;

internal sealed partial class WorkerDispatchTestsSeededRepositoryFactory
{
    private RepositoryIdentity ValidateTemplateStage(
        TemplateState template,
        ValidationStage stage,
        ValidationCheck identityCheck,
        RepositoryIdentity priorIdentity,
        string? stagingPath,
        string? finalPath,
        FixtureAttempt attempt)
    {
        // The construction digest is immutable. Never refresh it after a cache miss.
        if (_hooks.BeforeTemplateValidation is null &&
            template.ContentDigest is not null &&
            string.Equals(ComputeTemplateDigest(template.Path), template.ContentDigest, StringComparison.Ordinal))
        {
            return priorIdentity;
        }

        var identity = ValidateRepository(
            template.Path, stage, template.Path, stagingPath, finalPath,
            priorIdentity, attempt: attempt);
        EnsureSameIdentity(
            identityCheck, priorIdentity, identity, template.Path, stagingPath, finalPath, attempt);
        return identity;
    }

    private static string? ComputeTemplateDigest(string path)
    {
        try
        {
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var entries = Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories)
                .Select(entry => (Path: entry, Relative: Path.GetRelativePath(path, entry).Replace('\\', '/')))
                .OrderBy(entry => entry.Relative, StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                var isDirectory = Directory.Exists(entry.Path);
                var name = Encoding.UTF8.GetBytes(entry.Relative);
                digest.AppendData([isDirectory ? (byte)0 : (byte)1]);
                digest.AppendData(BitConverter.GetBytes(name.Length));
                digest.AppendData(name);
                if (!isDirectory)
                {
                    using var file = new FileStream(
                        entry.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    digest.AppendData(SHA256.HashData(file));
                }
            }

            return Convert.ToHexString(digest.GetHashAndReset());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A missing or unreadable entry forces the existing typed validation path.
            return null;
        }
    }

    private (string TopLevel, string GitDirectory) ValidateCollapsedLocation(
        string path,
        ValidationStage stage,
        string sourceTemplatePath,
        string? stagingPath,
        string? finalPath,
        FileSystemObservation observation,
        RepositoryIdentity? templateIdentity,
        RepositoryIdentity? stagingIdentity,
        FixtureAttempt attempt)
    {
        // Empty output and process faults retain the required TopLevel probe's routing.
        var result = RequiredProbe(
            path, ["rev-parse", "--is-inside-work-tree", "--show-toplevel", "--git-dir"],
            Check(stage, "TopLevel"), sourceTemplatePath, stagingPath, finalPath,
            observation, templateIdentity, stagingIdentity, attempt);
        var lines = result.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var lineCount = lines.Length;
        if (lineCount > 0 && lines[^1].Length == 0)
        {
            lineCount--;
        }

        string Line(int index) => index < lineCount ? lines[index] : string.Empty;

        void Reject(GitProbeResult receipt, string suffix, string rawOutput)
        {
            receipt = attempt.Reclassify(receipt, rawOutput.Length == 0
                ? GitProbeClassification.EmptyRequiredOutput
                : GitProbeClassification.InvalidRequiredOutput);
            throw Failure(
                Check(stage, suffix), sourceTemplatePath, stagingPath, finalPath,
                _fileSystem.ObserveRepository(path), receipt, templateIdentity, stagingIdentity,
                probeReceipts: attempt.Receipts);
        }

        var inside = attempt.Record(result, path, Check(stage, "InsideWorkTree"));
        var rawInside = Line(0);
        if (!string.Equals(rawInside.Trim(), "true", StringComparison.Ordinal))
        {
            Reject(inside, "InsideWorkTree", rawInside);
        }

        var rawTopLevel = Line(1);
        if (string.IsNullOrWhiteSpace(rawTopLevel))
        {
            Reject(result, "TopLevel", rawTopLevel);
        }
        var topLevel = CanonicalPath(rawTopLevel.Trim());
        if (!string.Equals(topLevel, path, PathComparison))
        {
            Reject(result, "TopLevel", rawTopLevel);
        }

        var gitDirectoryResult = attempt.Record(result, path, Check(stage, "GitDirectory"));
        var rawGitDirectory = Line(2);
        if (string.IsNullOrWhiteSpace(rawGitDirectory))
        {
            Reject(gitDirectoryResult, "GitDirectory", rawGitDirectory);
        }
        var gitDirectory = CanonicalPath(Path.IsPathRooted(rawGitDirectory.Trim())
            ? rawGitDirectory.Trim()
            : Path.Combine(path, rawGitDirectory.Trim()));
        if (!string.Equals(gitDirectory, CanonicalPath(Path.Combine(path, ".git")), PathComparison) ||
            lineCount != 3)
        {
            Reject(gitDirectoryResult, "GitDirectory", rawGitDirectory);
        }

        return (topLevel, gitDirectory);
    }
}
