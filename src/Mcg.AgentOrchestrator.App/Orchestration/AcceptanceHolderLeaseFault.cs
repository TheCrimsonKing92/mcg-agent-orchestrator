using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// A read failure is evidence for a capacity hold, never authority to settle a holder.
internal sealed record AcceptanceHolderLeaseFault(string Reason, string Expected, string Recorded)
{
    private const string KindKey = "AcceptanceHolderLease.FaultKind";
    private const string ExpectedKey = "AcceptanceHolderLease.Expected";
    private const string RecordedKey = "AcceptanceHolderLease.Recorded";

    internal static AcceptanceHolderLeaseFault? From(Exception exception, AcceptanceHolderBinding expected) =>
        exception is JsonException or InvalidDataException
            ? new(exception.Data[KindKey] as string ?? "lease-malformed",
                exception.Data[ExpectedKey] as string ?? Describe(expected),
                exception.Data[RecordedKey] as string ?? "unreadable")
            : null;

    internal string Format() => $"{Reason} expected={Expected} recorded={Recorded}";

    internal static T Annotate<T>(T exception, string kind, string expected, string recorded) where T : Exception
    {
        exception.Data[KindKey] = kind;
        exception.Data[ExpectedKey] = expected;
        exception.Data[RecordedKey] = recorded;
        return exception;
    }

    internal static string Describe(AcceptanceHolderBinding? binding, SpawnProcessIdentity? holder = null) =>
        $"attempt:{Token(binding?.AttemptId)},slot:{Token(binding?.Slot)},candidate:{Token(binding?.Candidate)}" +
        (holder is null ? "" : $",pid:{holder.ProcessId},start:{holder.StartedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}");

    internal static string Token(string? value) => string.IsNullOrEmpty(value) ? "-" :
        new string(value.Select(c => char.IsWhiteSpace(c) || c == '=' ? '_' : c).ToArray());
}
