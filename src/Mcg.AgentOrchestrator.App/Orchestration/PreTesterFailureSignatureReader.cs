using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class PreTesterFailureSignatureReader
{
    internal static string AppendTo(
        string indexLine, string outcome, IReadOnlyList<string> failingTests,
        IReadOnlyList<string>? trxPaths, Func<string, AcceptanceTrxReadResult>? readTrx = null)
    {
        if (outcome != "actionable-red") return indexLine;
        try
        {
            var identities = failingTests.Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal).ToArray();
            var allowed = identities.ToHashSet(StringComparer.Ordinal);
            var failures = new Dictionary<string, AcceptanceTrxFailure>(StringComparer.Ordinal);
            foreach (var path in trxPaths ?? [])
            {
                var result = (readTrx ?? AcceptanceTrxFailureReader.Read)(path);
                if (result.Status != AcceptanceTrxReadStatus.Readable) continue;
                foreach (var failure in result.Failures)
                    if (failure.TestName is { } name && allowed.Contains(name))
                        failures.TryAdd(name, failure);
            }
            var signatures = identities.Where(failures.ContainsKey).Select(name =>
                new KeyValuePair<string, string>(name, PreTesterFailureSignature.Describe(
                    failures[name].Message, failures[name].StackTrace))).ToArray();
            return PreTesterFailureSignature.AppendField(indexLine, signatures);
        }
        catch (Exception)
        {
            return indexLine;
        }
    }
}
