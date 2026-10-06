using Mcg.AgentOrchestrator.Core;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    private static IReadOnlySet<string>? ValidDeclaredRevertPaths(IReadOnlyList<string>? paths)
    {
        if (paths is null) return null;
        var declaration = NegativeControlRevertDeclaration.Parse("negative-control-revert: " + string.Join(',', paths));
        return declaration.Paths.ToHashSet(StringComparer.Ordinal);
    }

    private static string[] NegativeControlDiffPathspecs(IReadOnlyList<string>? revertPaths,
        FindingEvidenceMutation? mutation, IReadOnlySet<string>? declared) =>
        ["src/", .. (revertPaths is null ? [] : NegativeControlRevertPolicy.RevertablePolicyFiles),
            .. (revertPaths is null && mutation is null ? [] : declared ?? (IEnumerable<string>)[])];

    private static bool IsDeclaredRevertPath(string path, IReadOnlySet<string>? declared) =>
        declared?.Contains(path) == true;

    private static FocusedEvidenceArmRunResult WithSourceRevertedPathReceipt(FocusedEvidenceArmRunResult arm,
        IReadOnlyList<string>? restored, IReadOnlyList<string>? dropped) => arm with
    {
        RestoredPaths = restored?.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
        DroppedPaths = dropped?.OrderBy(path => path, StringComparer.Ordinal).ToArray()
    };

    private static FindingEvidenceRevertPathsRejection? ValidateSelectedTestClassPaths(
        IEnumerable<string> paths, string[] diff, string candidatePath, string candidateSha, string mergeBase,
        IReadOnlyList<AcceptanceManifestCheck> checks, IReadOnlySet<string>? declared)
    {
        // Preserve callers without the new declaration contract.
        if (declared is null) return null;
        var names = checks.SelectMany(check => check.FocusedEvidenceMatchedClasses)
            .SelectMany(selection => selection.ClassNames).Select(name => name.Split('.')[^1])
            .Concat(checks.SelectMany(check => check.FocusedEvidenceTokens)
                .Where(token => token.Kind is FocusedEvidenceTokenKind.Class or FocusedEvidenceTokenKind.Method)
                .Select(token => token.ContainingClass.Split('.')[^1]))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var path in paths.Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            var index = Array.FindIndex(diff, 1, value => string.Equals(value, path, StringComparison.Ordinal));
            if (index < 1 || index % 2 != 1) throw new InvalidDataException("Selected-class path is absent from the goal diff.");
            var status = diff[index - 1];
            foreach (var sha in status switch
            {
                "A" => new[] { candidateSha },
                "D" => new[] { mergeBase },
                "M" or "T" => new[] { candidateSha, mergeBase },
                _ => throw new InvalidDataException("Unsupported selected-class source diff status.")
            })
            {
                var source = GitCli.Run(candidatePath, "show", $"{sha}:{path}");
                if (!source.Succeeded || source.DrainTimedOut)
                    throw new InvalidDataException("Selected-class source text could not be read.");
                var syntax = CSharpSyntaxTree.ParseText(source.Output, path: path).GetCompilationUnitRoot();
                if (syntax.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
                    .Any(type => names.Contains(type.Identifier.ValueText)) ||
                    syntax.DescendantNodes().OfType<DelegateDeclarationSyntax>()
                    .Any(type => names.Contains(type.Identifier.ValueText)))
                    return FindingEvidenceRevertPathsRejection.SelectedTestClass;
            }
        }
        return null;
    }
}
