using Mcg.AgentOrchestrator.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record ReviewerChangedExistingTestRead(IReadOnlyList<ChangedExistingTest> Entries, string? Diagnostic);

internal static class ReviewerChangedExistingTestReader
{
    internal static ReviewerChangedExistingTestRead Read(
        string workingDirectory, string? mergeBase, string? headCommit, IReadOnlyList<string>? changedPaths,
        Func<string, string[], GitCli.GitResult>? git = null)
    {
        try
        {
            var paths = (changedPaths ?? []).Select(path => path.Replace('\\', '/'))
                .Where(path => path.StartsWith("tests/", StringComparison.OrdinalIgnoreCase) &&
                    path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (paths.Length == 0) return new([], null);
            if (string.IsNullOrWhiteSpace(mergeBase))
                return new([], "Changed existing tests unavailable because the reviewer merge base is missing.");
            if (string.IsNullOrWhiteSpace(headCommit))
                return new([], "Changed existing tests unavailable because the candidate head is missing.");
            git ??= (directory, args) => GitCli.Run(directory, args);
            // Resolve once: comparisons must use these immutable commits, even if a branch moves.
            var baseline = Run("rev-parse", "--verify", mergeBase + "^{commit}").Trim();
            var candidate = Run("rev-parse", "--verify", headCommit + "^{commit}").Trim();
            if (baseline.Length == 0 || candidate.Length == 0)
                throw new IOException("git rev-parse returned an empty commit identity.");
            var entries = new List<ChangedExistingTest>();
            foreach (var path in paths)
            {
                var before = Methods(ReadFile(baseline, path));
                var after = Methods(ReadFile(candidate, path));
                foreach (var (key, method) in before)
                {
                    var removed = !after.TryGetValue(key, out var headMethod);
                    if (!removed && method.IsEquivalentTo(headMethod, topLevel: false)) continue;
                    var location = removed ? method : headMethod!;
                    var span = location.SyntaxTree.GetLineSpan(location.Span);
                    entries.Add(new(TypeName(method), method.Identifier.ValueText, path,
                        span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1, removed));
                }
            }
            return new(entries, null);

            string Run(params string[] args)
            {
                var result = git(workingDirectory, args);
                if (!result.Succeeded || !result.ProcessStarted || result.DrainTimedOut)
                    throw new IOException($"git {string.Join(" ", args)} failed (exit {result.ExitCode}, started={result.ProcessStarted}, drainTimedOut={result.DrainTimedOut}): {result.Error}");
                return result.Output;
            }

            string ReadFile(string commit, string path)
            {
                // Absence is a normal addition/deletion; failures of ls-tree/show are not absence.
                if (string.IsNullOrEmpty(Run("ls-tree", "--name-only", "-z", commit, "--", path))) return string.Empty;
                return Run("show", commit + ":" + path);
            }
        }
        catch (Exception exception)
        {
            // Partial results would falsely present a complete inventory to the Reviewer.
            return new([], "Changed existing tests unavailable: " + exception.Message.ReplaceLineEndings(" "));
        }
    }

    private static Dictionary<string, MethodDeclarationSyntax> Methods(string text) =>
        CSharpSyntaxTree.ParseText(text).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .ToDictionary(method => TypeName(method) + "." + method.Identifier.ValueText + "(" +
                string.Join(",", method.ParameterList.Parameters.Select(parameter =>
                    string.Concat(parameter.Type?.DescendantTokens().Select(token => token.Text) ?? []))) + ")",
                StringComparer.Ordinal);

    private static string TypeName(MethodDeclarationSyntax method) =>
        method.Ancestors().OfType<BaseTypeDeclarationSyntax>().First().Identifier.ValueText;
}
