using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class FindingEvidenceNameSelector
{
    private static readonly Regex NameOperandPattern = new(
        @"(?<![A-Za-z0-9_])Name~",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static string ToFullyQualified(string filter) =>
        NameOperandPattern.Replace(filter, "FullyQualifiedName~");
}
