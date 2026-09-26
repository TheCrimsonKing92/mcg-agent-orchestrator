using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class AdjudicationEvidenceResolver(string? orchestratorDirectory = null)
{
    public bool TryResolve(
        string reference,
        Goal goal,
        AdjudicateOperatorIntentPayload payload,
        out EvidenceManifestEntry entry)
    {
        var colon = reference.IndexOf(':');
        var kind = colon > 0 ? reference[..colon].ToLowerInvariant() : string.Empty;
        var value = colon > 0 ? reference[(colon + 1)..] : string.Empty;
        try
        {
            switch (kind)
            {
                case "trx":
                case "operator-evidence":
                {
                    if (string.IsNullOrWhiteSpace(value)) break;
                    var path = Path.IsPathFullyQualified(value)
                        ? Path.GetFullPath(value)
                        : Path.GetFullPath(value, payload.WorkingDirectory);
                    if (kind == "trx" && !path.EndsWith(".trx", StringComparison.OrdinalIgnoreCase)) break;
                    if (!File.Exists(path)) break;
                    if (kind == "trx" && XDocument.Load(path).Root?.Name.LocalName != "TestRun") break;
                    entry = new EvidenceManifestEntry(reference, Hash(File.ReadAllBytes(path)));
                    return true;
                }
                case "focused-evidence":
                {
                    var receipt = goal.Tasks.SelectMany(task => task.PreReviewEvidenceHistory)
                        .FirstOrDefault(item => !string.IsNullOrWhiteSpace(value) &&
                            (string.Equals(item.EvidencePointer, value, StringComparison.Ordinal) ||
                             string.Equals(item.CandidateSha, value, StringComparison.OrdinalIgnoreCase)));
                    if (receipt is null) break;
                    entry = new EvidenceManifestEntry(reference, Hash(JsonSerializer.SerializeToUtf8Bytes(receipt)));
                    return true;
                }
                case "acceptance-attempt":
                {
                    if (string.IsNullOrWhiteSpace(orchestratorDirectory) || string.IsNullOrWhiteSpace(value)) break;
                    var attempt = GoalTerminalReconciliationEvidenceResolver.ResolveForGoal(
                            Path.Combine(orchestratorDirectory, "acceptance-gate-attempts"), goal.Id)
                        .FirstOrDefault(item => string.Equals(item.AttemptId, value, StringComparison.Ordinal) &&
                            item.State is GoalTerminalReconciliationEvidenceState.Present or
                                GoalTerminalReconciliationEvidenceState.MissingByInProcessProtocol);
                    if (attempt is null) break;
                    entry = new EvidenceManifestEntry(reference, Hash(File.ReadAllBytes(attempt.AttemptMetadataPath)));
                    return true;
                }
                default:
                {
                    if (colon > 1 && kind.All(ch => char.IsLetterOrDigit(ch) || ch == '-')) break;
                    var separator = reference.IndexOf('=');
                    if (separator > 0 && separator < reference.Length - 1 &&
                        reference.IndexOf('=', separator + 1) < 0)
                        entry = new EvidenceManifestEntry(reference[..separator], reference[(separator + 1)..]);
                    else
                        entry = new EvidenceManifestEntry(reference, Hash(Encoding.UTF8.GetBytes(reference)));
                    return true;
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (ArgumentException) { }
        catch (System.Xml.XmlException) { }
        entry = new EvidenceManifestEntry(reference, Hash(Encoding.UTF8.GetBytes(reference)));
        return false;
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
