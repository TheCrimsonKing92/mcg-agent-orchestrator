using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static partial class GoalWorktrees
{
    private const int MaximumDiagnosticStreamChars = 512;
    private const int MaximumDiagnosticStatusLines = 25;
    private const int MaximumDiagnosticStatusLineChars = 512;
    private const int MaximumDiagnosticCandidates = 8;

    private sealed record MaterializationFileSnapshot(string Length, string Sha256, string LastWriteUtc);

    private sealed record RematerializationAttempt(
        string RelativePath,
        MaterializationFileSnapshot Before,
        MaterializationFileSnapshot After,
        GitCli.GitResult Checkout);

    private enum AfterWriteFailureKind { Other, StillDirty, BytesMismatch }

    private static MaterializationFileSnapshot CaptureMaterializationSnapshot(string fullPath)
    {
        try
        {
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length.ToString(CultureInfo.InvariantCulture);
            var sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            var lastWriteUtc = File.GetLastWriteTimeUtc(fullPath).ToString("o", CultureInfo.InvariantCulture);
            return new MaterializationFileSnapshot(length, sha256, lastWriteUtc);
        }
        catch (FileNotFoundException)
        {
            return new MaterializationFileSnapshot("absent", "absent", "absent");
        }
        catch (DirectoryNotFoundException)
        {
            return new MaterializationFileSnapshot("absent", "absent", "absent");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var detail = BoundDiagnostic(EscapeDiagnostic(ex.Message), MaximumDiagnosticStreamChars);
            return new MaterializationFileSnapshot("unreadable: " + detail, "unreadable", "unreadable");
        }
    }

    private static string DescribeRematerializationEvidence(
        string worktreePath,
        IReadOnlyList<RematerializationAttempt> attempts,
        string dirtyStatus,
        Func<string, string[], GitCli.GitResult> gitRunner)
    {
        var evidence = new StringBuilder("; rematerialization evidence:");
        foreach (var attempt in attempts.Take(MaximumDiagnosticCandidates))
        {
            var path = attempt.RelativePath;
            evidence.Append(" candidate path='").Append(BoundDiagnostic(EscapeDiagnostic(path), MaximumDiagnosticStatusLineChars))
                .Append("' snapshot=before-checkout-after-stat-invalidation")
                .Append(" before.length=").Append(attempt.Before.Length)
                .Append("; before.sha256=").Append(attempt.Before.Sha256)
                .Append("; before.lastWriteUtc=").Append(attempt.Before.LastWriteUtc)
                .Append("; after.length=").Append(attempt.After.Length)
                .Append("; after.sha256=").Append(attempt.After.Sha256)
                .Append("; after.lastWriteUtc=").Append(attempt.After.LastWriteUtc)
                .Append("; checkout.exit=").Append(attempt.Checkout.ExitCode)
                .Append("; checkout.stdout=").Append(BoundDiagnostic(EscapeDiagnostic(attempt.Checkout.Output), MaximumDiagnosticStreamChars))
                .Append("; checkout.stderr=").Append(BoundDiagnostic(EscapeDiagnostic(attempt.Checkout.Error), MaximumDiagnosticStreamChars))
                .Append("; lsFilesEol=").Append(Probe("--eol"))
                .Append("; lsFilesDebug=").Append(Probe("--debug"));

            string Probe(string option)
            {
                try
                {
                    var result = gitRunner(worktreePath, ["ls-files", option, "--", path]);
                    return $"exit={result.ExitCode}; stdout={BoundDiagnostic(EscapeDiagnostic(result.Output), MaximumDiagnosticStreamChars)}; stderr={BoundDiagnostic(EscapeDiagnostic(result.Error), MaximumDiagnosticStreamChars)}";
                }
                catch (Exception ex)
                {
                    return "unreadable: " + BoundDiagnostic(EscapeDiagnostic(ex.Message), MaximumDiagnosticStreamChars);
                }
            }
        }

        if (attempts.Count > MaximumDiagnosticCandidates)
        {
            evidence.Append(" (+").Append(attempts.Count - MaximumDiagnosticCandidates).Append(" more candidates omitted)");
        }

        if (dirtyStatus.Length > 0)
        {
            var lines = dirtyStatus.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            evidence.Append(" || statusLines=[");
            for (var index = 0; index < Math.Min(lines.Length, MaximumDiagnosticStatusLines); index++)
            {
                if (index > 0) evidence.Append(", ");
                evidence.Append('\'').Append(BoundDiagnostic(EscapeDiagnostic(lines[index]), MaximumDiagnosticStatusLineChars)).Append('\'');
            }
            if (lines.Length > MaximumDiagnosticStatusLines)
            {
                evidence.Append(" (+").Append(lines.Length - MaximumDiagnosticStatusLines).Append(" more status lines omitted)");
            }
            evidence.Append(']');
        }

        return evidence.ToString();
    }

    private static string BoundDiagnostic(string value, int cap) =>
        value.Length <= cap ? value : value[..cap] + $"...[truncated from {value.Length} chars]";

    private static string EscapeDiagnostic(string? value)
    {
        if (value is null) return "<null>";
        var escaped = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsControl(character))
            {
                escaped.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
            }
            else
            {
                escaped.Append(character);
            }
        }
        return escaped.ToString();
    }
}
