using System.Security.Cryptography;
using System.Text;

namespace Mcg.AgentOrchestrator.Core;

public sealed record GoalHoldState(
    string Identity,
    string State,
    string Blocker,
    DateTimeOffset StartedAt,
    DateTimeOffset? StalledAt = null)
{
    internal static string BuildIdentity(string state, string blocker, string? stableIdentity = null)
    {
        var bytes = Encoding.UTF8.GetBytes($"{state}\0{stableIdentity ?? blocker}");
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}

public sealed record GoalHoldObservation(
    GoalHoldState Hold,
    bool StateChanged,
    bool BecameStalled);
