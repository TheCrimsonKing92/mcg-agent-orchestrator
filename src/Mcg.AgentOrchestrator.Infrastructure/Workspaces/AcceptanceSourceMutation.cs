using System.Text;
using Mcg.AgentOrchestrator.Core;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceSourceMutation
{
    internal static FindingEvidenceRevertPathsRejection? Validate(
        FindingEvidenceMutation mutation, string[] diff, string candidatePath, string candidateSha, string mergeBase,
        IReadOnlyList<AcceptanceManifestCheck> checks, IReadOnlySet<string>? declared,
        Func<string, FindingEvidenceRevertPathsRejection?> validatePath, Func<string, bool> isSelectedTestClass)
    {
        var path = FindingEvidenceRevertPaths.Normalize(mutation.Path);
        var pathRejection = validatePath(path);
        if (pathRejection is not null)
            return pathRejection switch
            {
                FindingEvidenceRevertPathsRejection.UnderTests => FindingEvidenceRevertPathsRejection.MutationUnderTests,
                FindingEvidenceRevertPathsRejection.OutsideSrc => FindingEvidenceRevertPathsRejection.MutationOutsideSrc,
                FindingEvidenceRevertPathsRejection.NotChangedByGoal => FindingEvidenceRevertPathsRejection.MutationNotChangedByGoal,
                _ => throw new InvalidOperationException("Unexpected mutation path rejection.")
            };
        if (isSelectedTestClass(path))
            return FindingEvidenceRevertPathsRejection.MutationSelectedTestClass;
        if (string.IsNullOrEmpty(mutation.OldText)) return FindingEvidenceRevertPathsRejection.MutationEmptyOldText;
        if (string.Equals(NormalizeLineBreaks(mutation.OldText),
            NormalizeLineBreaks(mutation.NewText ?? string.Empty), StringComparison.Ordinal))
            return FindingEvidenceRevertPathsRejection.MutationUnchangedText;
        var target = ResolveTarget(candidatePath, path);
        if (!File.Exists(target)) return FindingEvidenceRevertPathsRejection.MutationOldTextNotFound;
        var (_, _, text) = ReadFile(target);
        return FindSpan(text, mutation.OldText, out _);
    }

    private static FindingEvidenceRevertPathsRejection? FindSpan(string text, string oldText, out int start) =>
        FindSpan(text, oldText, out start, out _);

    private static FindingEvidenceRevertPathsRejection? FindSpan(
        string text, string oldText, out int start, out int length)
    {
        // Break-free needles retain exact matching, even a lone CR at the edge of a CRLF.
        var comparison = oldText.Contains('\n') ? NormalizeLineBreaks(text) : text;
        var needle = NormalizeLineBreaks(oldText);
        start = comparison.IndexOf(needle, StringComparison.Ordinal);
        length = needle.Length;
        if (start < 0) return FindingEvidenceRevertPathsRejection.MutationOldTextNotFound;
        if (comparison.IndexOf(needle, start + needle.Length, StringComparison.Ordinal) >= 0)
            return FindingEvidenceRevertPathsRejection.MutationOldTextAmbiguous;
        if (!oldText.Contains('\n')) return null;
        var offsets = new int[comparison.Length + 1];
        var normalizedOffset = 0;
        for (var originalOffset = 0; originalOffset < text.Length; originalOffset++)
        {
            offsets[normalizedOffset++] = originalOffset;
            if (text[originalOffset] == '\r' && originalOffset + 1 < text.Length && text[originalOffset + 1] == '\n')
                originalOffset++;
        }
        offsets[normalizedOffset] = text.Length;
        length = offsets[start + needle.Length] - offsets[start];
        start = offsets[start];
        return null;
    }

    private static string NormalizeLineBreaks(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    // Called only after the detached candidate worktree has been created and labelled.
    internal static void Apply(string revertedPath, FindingEvidenceMutation mutation)
    {
        var target = ResolveTarget(revertedPath, FindingEvidenceRevertPaths.Normalize(mutation.Path));
        var (bytes, encoding, text) = ReadFile(target);
        if (FindSpan(text, mutation.OldText, out var start, out var length) is not null)
            throw new InvalidDataException("Mutation no longer has exactly one old_text occurrence in the temporary worktree.");
        var preambleLength = bytes.Length - encoding.GetByteCount(text);
        var byteStart = preambleLength + encoding.GetByteCount(text.AsSpan(0, start));
        var matchedSpan = text.AsSpan(start, length);
        var byteEnd = byteStart + encoding.GetByteCount(matchedSpan);
        var replacementText = NormalizeLineBreaks(mutation.NewText ?? string.Empty);
        if (matchedSpan.Contains("\r\n", StringComparison.Ordinal))
            replacementText = replacementText.Replace("\n", "\r\n", StringComparison.Ordinal);
        var replacement = encoding.GetBytes(replacementText);
        var result = new byte[byteStart + replacement.Length + bytes.Length - byteEnd];
        bytes.AsSpan(0, byteStart).CopyTo(result);
        replacement.CopyTo(result, byteStart);
        bytes.AsSpan(byteEnd).CopyTo(result.AsSpan(byteStart + replacement.Length));
        File.WriteAllBytes(target, result);
    }

    private static (byte[] Bytes, Encoding Encoding, string Text) ReadFile(string target)
    {
        var bytes = File.ReadAllBytes(target);
        using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        _ = reader.ReadToEnd();
        var encoding = (Encoding)reader.CurrentEncoding.Clone();
        encoding.DecoderFallback = DecoderFallback.ExceptionFallback;
        encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
        var preamble = encoding.GetPreamble();
        var offset = preamble.Length > 0 && bytes.AsSpan().StartsWith(preamble) ? preamble.Length : 0;
        // Strict decoding prevents a lossy read from manufacturing an exact match or changing unrelated bytes.
        return (bytes, encoding, encoding.GetString(bytes, offset, bytes.Length - offset));
    }

    private static string ResolveTarget(string worktreePath, string path)
    {
        var root = Path.GetFullPath(worktreePath);
        var target = Path.GetFullPath(Path.Combine(root, path));
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Source mutation escaped the worktree.");
        for (var current = target; !string.Equals(current, root, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current)!)
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Source mutation cannot traverse a symbolic link or reparse point.");
        return target;
    }
}
