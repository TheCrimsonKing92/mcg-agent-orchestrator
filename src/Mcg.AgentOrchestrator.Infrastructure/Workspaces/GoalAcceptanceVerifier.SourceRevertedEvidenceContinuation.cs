using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Invocation-local verifier handoff. Only the host owns durable observations and decides reuse.
internal sealed class SourceRevertedEvidenceContinuation(
    Func<string, FocusedEvidenceRunResult?> resume,
    Action<string, FocusedEvidenceRunResult> observe,
    Action<bool> deferred)
{
    internal bool RestorationVerified { get; set; }
    internal FocusedEvidenceRunResult? Resume(string identity) => resume(identity);
    internal void Observe(string identity, FocusedEvidenceRunResult evidence) => observe(identity, evidence);
    internal void Deferred() => deferred(RestorationVerified);

    internal static string Fingerprint<T>(T value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
}
