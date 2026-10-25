using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class TestClassRenameEvidence
{
    internal sealed record ClassRename(
        string OldClass, string NewClass, string OldPath, string NewPath, int Similarity, string Evidence);

    // A link is git's file identity evidence; class identity is checked at the candidate endpoint.
    private sealed record Link(string OldPath, string NewPath, int Similarity, string OldRevision, string Evidence);

    internal static IReadOnlyList<ClassRename> Read(
        string repositoryDirectory, string integrationBranch, string candidateSha,
        Func<string, string[], GitCli.GitResult>? runGit = null)
    {
        runGit ??= GitCli.Run;
        try
        {
            string Git(params string[] args)
            {
                var result = runGit(repositoryDirectory, args);
                if (!result.Succeeded || result.DrainTimedOut) throw new InvalidOperationException("Git evidence unavailable.");
                return result.Output;
            }

            var mergeBase = Git("merge-base", integrationBranch, candidateSha).Trim();
            var commits = ParseLinks(Git("log", "--reverse", "--no-merges", "--find-renames", "--diff-filter=R",
                "--name-status", "-z", "--format=%H", $"{mergeBase}..{candidateSha}", "--", "*.cs"));
            var tree = ParseLinks(Git("diff", "--find-renames", "--diff-filter=R", "--name-status", "-z",
                mergeBase, candidateSha, "--", "*.cs"), mergeBase, $"tree {mergeBase}..{candidateSha}");
            if (commits.Count + tree.Count == 0) return [];

            var paths = Git("ls-tree", "-r", "--name-only", "-z", candidateSha)
                .Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            var nextPaths = commits.GroupBy(link => link.OldPath, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Select(link => link.NewPath)
                    .Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
            var ordered = commits.Select((link, position) => (Link: link, Position: position)).ToArray();
            var classes = new Dictionary<(string Revision, string Path), HashSet<string>>();
            HashSet<string> Classes(string revision, string path)
            {
                if (classes.TryGetValue((revision, path), out var cached)) return cached;
                var syntax = CSharpSyntaxTree.ParseText(Git("show", $"{revision}:{path}").TrimStart('\uFEFF'));
                if (syntax.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
                    throw new InvalidOperationException("Invalid C# rename evidence.");
                return classes[(revision, path)] = syntax.GetRoot().DescendantNodes()
                    .OfType<ClassDeclarationSyntax>().Select(declaration => declaration.Identifier.ValueText)
                    .ToHashSet(StringComparer.Ordinal);
            }

            var renames = new List<ClassRename>();
            // Tree evidence wins when both sources prove the same endpoint mapping.
            foreach (var (link, position) in tree.Select(link => (Link: link, Position: -1)).Concat(ordered))
            {
                if (nextPaths.TryGetValue(link.OldPath, out var origins) && origins.Length != 1) continue;
                var terminal = link.NewPath;
                var visited = new HashSet<string>(StringComparer.Ordinal) { link.OldPath };
                var cursor = position;
                var valid = true;
                // Tree links already describe endpoints. Commit links can follow only later history.
                while (cursor >= 0)
                {
                    var following = ordered.Where(item => item.Position > cursor && item.Link.OldPath == terminal).ToArray();
                    if (following.Length == 0) break;
                    if (following.Length != 1 || paths.Contains(terminal) || !visited.Add(terminal))
                    {
                        valid = false;
                        break;
                    }
                    terminal = following[0].Link.NewPath;
                    cursor = following[0].Position;
                }
                if (valid && !visited.Contains(terminal) &&
                    !paths.Contains(link.OldPath) && paths.Contains(terminal))
                {
                    var oldClasses = Classes(link.OldRevision, link.OldPath);
                    var newClasses = Classes(candidateSha, terminal);
                    var removed = oldClasses.Except(newClasses, StringComparer.Ordinal).ToArray();
                    var added = newClasses.Except(oldClasses, StringComparer.Ordinal).ToArray();
                    if (removed.Length == 1 && added.Length == 1)
                        renames.Add(new(removed[0], added[0], link.OldPath, terminal, link.Similarity, link.Evidence));
                }
            }

            return renames.DistinctBy(rename => (rename.OldClass, rename.NewClass, rename.OldPath, rename.NewPath))
                .GroupBy(rename => rename.OldClass, StringComparer.Ordinal)
                .Where(group => group.Count() == 1).Select(group => group.Single()).ToArray();
        }
        catch (Exception)
        {
            // Incomplete git output or recovered syntax cannot authorize a landing.
            return [];
        }
    }

    private static IReadOnlyList<Link> ParseLinks(string output, string? oldRevision = null, string? evidence = null)
    {
        var tokens = output.Split('\0');
        var links = new List<Link>();
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index].Trim('\r', '\n');
            if (token.Length == 0) continue;
            if (token.Length is 40 or 64 && token.All(Uri.IsHexDigit))
            {
                oldRevision = token + "^";
                evidence = "commit " + token;
                continue;
            }
            if (token[0] != 'R' || !int.TryParse(token.AsSpan(1), out var similarity) ||
                similarity is < 50 or > 100 || index + 2 >= tokens.Length || oldRevision is null || evidence is null)
                throw new InvalidOperationException("Invalid git rename output.");
            var oldPath = tokens[++index];
            var newPath = tokens[++index];
            if (oldPath.Length == 0 || newPath.Length == 0) throw new InvalidOperationException("Missing rename path.");
            links.Add(new(oldPath, newPath, similarity, oldRevision, evidence));
        }
        return links;
    }
}
