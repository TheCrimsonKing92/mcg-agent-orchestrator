using Mcg.AgentOrchestrator.Core;
using static Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Owns the diagnostic receipts appended to completed command-check summaries.
internal static class AcceptanceCommandResultSummary
{
    internal static string? ManagedDotnet(CommandResult result, string? summary, bool transientCompilerLockRetried)
    {
        summary = AppendResourceReceipt(summary, result.ResourceAccounting, result.ResourceAccountingExpected);
        if (!transientCompilerLockRetried)
        {
            return summary;
        }

        const string remediation = "build artifact lock detected; holder attributed; remediation retried";
        return string.IsNullOrWhiteSpace(summary)
            ? remediation
            : $"{remediation}; {summary}";
    }

    internal static string? GenericCommand(CommandResult result, string? summary)
    {
        if (result.ResolvedCommandPath is { } resolvedPath)
        {
            var receipt = $"resolved_command={ProcessInvokerAccess.QuoteForDisplay(resolvedPath)}";
            summary = string.IsNullOrWhiteSpace(summary) ? receipt : $"{summary}; {receipt}";
        }
        return AppendResourceReceipt(summary, result.ResourceAccounting, result.ResourceAccountingExpected);
    }

    private static string? AppendResourceReceipt(
        string? summary,
        TaskProcessResourceAccounting? accounting,
        bool accountingExpected)
    {
        var receipt = accounting is null
            ? accountingExpected ? "RESOURCE phase=gate cpu_ms=0 peak_mem_bytes=0 io_bytes=0 accounting_source=accounting-unavailable" : null
            : $"RESOURCE phase=gate cpu_ms={accounting.CpuMilliseconds} peak_mem_bytes={accounting.PeakMemoryBytes} io_bytes={accounting.IoBytes} accounting_source={accounting.AccountingSource}";
        if (receipt is null)
        {
            return summary;
        }

        return string.IsNullOrWhiteSpace(summary)
            ? receipt
            : $"{summary}; {receipt}";
    }
}
