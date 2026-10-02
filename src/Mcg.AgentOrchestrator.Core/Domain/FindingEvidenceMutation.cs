using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.Core;

/// <summary>A single exact-text perturbation requested for the conductor's temporary source arm.</summary>
public sealed record FindingEvidenceMutation(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("old_text")] string OldText,
    [property: JsonPropertyName("new_text")] string NewText)
{
    public static bool Same(FindingEvidenceMutation? left, FindingEvidenceMutation? right) =>
        left is null || right is null ? left is null && right is null :
        string.Equals(FindingEvidenceRevertPaths.Normalize(left.Path), FindingEvidenceRevertPaths.Normalize(right.Path), StringComparison.Ordinal) &&
        string.Equals(left.OldText, right.OldText, StringComparison.Ordinal) &&
        string.Equals(left.NewText, right.NewText, StringComparison.Ordinal);

    public static string IdentityJson(FindingEvidenceMutation mutation) =>
        JsonSerializer.Serialize(new[] { FindingEvidenceRevertPaths.Normalize(mutation.Path), mutation.OldText, mutation.NewText });

    public static string ShortHash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty)))[..12];
}
