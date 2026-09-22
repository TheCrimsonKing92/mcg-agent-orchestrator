using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>
/// One recorded infrastructure fault signature. Matching is string-based on purpose: these faults
/// reach the acceptance failure handler as TRX message and stack-trace text, never as a typed
/// exception, so the type name and a message marker are the only positive evidence available.
/// </summary>
internal sealed record ApparatusInfrastructureSignature(
    string Name,
    string ExceptionTypeName,
    string MessageMarker);

internal static class ApparatusInfrastructureSignatures
{
    internal const string EvidenceKind = "infrastructure-exception";

    // The recorded set starts deliberately small: every entry is a fault the repository already
    // classifies as apparatus elsewhere. Extending it is a one-line data change.
    internal static IReadOnlyList<ApparatusInfrastructureSignature> Recorded { get; } = Array.AsReadOnly(
        new[]
        {
            new ApparatusInfrastructureSignature(
                "dotnet-build-slots-busy",
                nameof(DotnetBuildSlotsBusyException),
                "Stable dotnet build slots busy"),
            new ApparatusInfrastructureSignature(
                "build-lock-blocked",
                nameof(BuildLockBlockedException),
                "Build artifact lock blocked progress"),
            new ApparatusInfrastructureSignature(
                "acceptance-infrastructure-deferred",
                nameof(AcceptanceInfrastructureDeferredException),
                "Acceptance infrastructure deferred")
        });

    /// <summary>
    /// Returns the recorded signature name when any supplied text carries positive evidence of a
    /// recorded infrastructure fault, or null when it does not. An absent signature never proves a
    /// candidate failure; it only removes the infrastructure-exception rule from the classifier.
    /// </summary>
    internal static string? Match(params string?[] texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        foreach (var signature in Recorded)
        {
            foreach (var text in texts)
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                if (text.Contains(signature.ExceptionTypeName, StringComparison.Ordinal) ||
                    text.Contains(signature.MessageMarker, StringComparison.Ordinal))
                {
                    return signature.Name;
                }
            }
        }

        return null;
    }
}
