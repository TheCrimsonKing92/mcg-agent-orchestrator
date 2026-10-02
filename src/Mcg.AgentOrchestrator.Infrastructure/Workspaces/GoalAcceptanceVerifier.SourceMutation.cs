using System.Text;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    private static FindingEvidenceRevertPathsRejection? ValidateSourceMutation(
        FindingEvidenceMutation mutation, string[] diff, string candidatePath)
    {
        var path = FindingEvidenceRevertPaths.Normalize(mutation.Path);
        var pathRejection = ValidateSourceRevertPaths([path], diff);
        if (pathRejection is not null)
            return pathRejection switch
            {
                FindingEvidenceRevertPathsRejection.UnderTests => FindingEvidenceRevertPathsRejection.MutationUnderTests,
                FindingEvidenceRevertPathsRejection.OutsideSrc => FindingEvidenceRevertPathsRejection.MutationOutsideSrc,
                FindingEvidenceRevertPathsRejection.NotChangedByGoal => FindingEvidenceRevertPathsRejection.MutationNotChangedByGoal,
                _ => throw new InvalidOperationException("Unexpected mutation path rejection.")
            };
        if (string.IsNullOrEmpty(mutation.OldText)) return FindingEvidenceRevertPathsRejection.MutationEmptyOldText;
        if (string.Equals(mutation.OldText, mutation.NewText, StringComparison.Ordinal))
            return FindingEvidenceRevertPathsRejection.MutationUnchangedText;
        var target = ResolveSourceMutationTarget(candidatePath, path);
        if (!File.Exists(target)) return FindingEvidenceRevertPathsRejection.MutationOldTextNotFound;
        var (_, _, text) = ReadSourceMutationFile(target);
        return FindSourceMutationSpan(text, mutation.OldText, out _);
    }

    private static FindingEvidenceRevertPathsRejection? FindSourceMutationSpan(string text, string oldText, out int start)
    {
        start = text.IndexOf(oldText, StringComparison.Ordinal);
        if (start < 0) return FindingEvidenceRevertPathsRejection.MutationOldTextNotFound;
        return text.IndexOf(oldText, start + oldText.Length, StringComparison.Ordinal) < 0 ? null :
            FindingEvidenceRevertPathsRejection.MutationOldTextAmbiguous;
    }

    // Called only after the detached candidate worktree has been created and labelled.
    private static void ApplySourceMutation(string revertedPath, FindingEvidenceMutation mutation)
    {
        var target = ResolveSourceMutationTarget(revertedPath, FindingEvidenceRevertPaths.Normalize(mutation.Path));
        var (bytes, encoding, text) = ReadSourceMutationFile(target);
        if (FindSourceMutationSpan(text, mutation.OldText, out var start) is not null)
            throw new InvalidDataException("Mutation no longer has exactly one old_text occurrence in the temporary worktree.");
        var preambleLength = bytes.Length - encoding.GetByteCount(text);
        var byteStart = preambleLength + encoding.GetByteCount(text.AsSpan(0, start));
        var byteEnd = byteStart + encoding.GetByteCount(mutation.OldText);
        var replacement = encoding.GetBytes(mutation.NewText ?? string.Empty);
        var result = new byte[byteStart + replacement.Length + bytes.Length - byteEnd];
        bytes.AsSpan(0, byteStart).CopyTo(result);
        replacement.CopyTo(result, byteStart);
        bytes.AsSpan(byteEnd).CopyTo(result.AsSpan(byteStart + replacement.Length));
        File.WriteAllBytes(target, result);
    }

    private static (byte[] Bytes, Encoding Encoding, string Text) ReadSourceMutationFile(string target)
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

    private static string ResolveSourceMutationTarget(string worktreePath, string path)
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
