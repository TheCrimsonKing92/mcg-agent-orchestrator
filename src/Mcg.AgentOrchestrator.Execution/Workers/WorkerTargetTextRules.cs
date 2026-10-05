namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Text-only target rules shared by dispatch evaluation and brief lint.</summary>
public static class WorkerTargetTextRules
{
    public static bool TargetsRepoScopedSkill(string text)
    {
        text = text.ToLowerInvariant();
        return text.Contains(".agents/skills", StringComparison.Ordinal) ||
            text.Contains(".agents\\skills", StringComparison.Ordinal);
    }

    // Matches .git after start of text or a non-filename character (not a letter/digit, '.', '_' or '-'),
    // and before end of text or a non-letter/digit, never .gitignore.
    public static int FindGitDirectoryReference(string text, int startIndex = 0)
    {
        text = text.ToLowerInvariant();
        var idx = startIndex;
        while ((idx = text.IndexOf(".git", idx, StringComparison.Ordinal)) >= 0)
        {
            var after = idx + 4;
            if ((idx == 0 || (!char.IsLetterOrDigit(text[idx - 1]) && text[idx - 1] is not ('.' or '_' or '-'))) &&
                (after >= text.Length || !char.IsLetterOrDigit(text[after])))
                return idx;
            idx = after;
        }
        return -1;
    }

    public static bool ContainsGitDirectoryReference(string text) => FindGitDirectoryReference(text) >= 0;

    public static int FindUnscopedSkillDefinition(string text) => TargetsRepoScopedSkill(text)
        ? -1
        : text.ToLowerInvariant().IndexOf("skill.md", StringComparison.Ordinal);

    /// <summary>The default text verdict, without a Git-reference override or launcher checks.</summary>
    public static bool IsDispatchBlocked(string text) =>
        ContainsGitDirectoryReference(text) || FindUnscopedSkillDefinition(text) >= 0;
}
