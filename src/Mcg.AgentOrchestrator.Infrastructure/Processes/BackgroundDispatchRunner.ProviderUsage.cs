using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class BackgroundDispatchRunner
{
    internal ClaudeTranscriptUsageReader ClaudeTranscriptUsage { get; init; } = ClaudeTranscriptUsageReader.CreateDefault();

    internal ClaudeTranscriptUsageResult ResolveDispatchProviderUsage(TaskDispatchRecord? dispatch, string standardOutputPath)
    {
        var codex = NormalizeStructuredCodexOutput(dispatch, standardOutputPath);
        if (codex is not null)
            return new(codex.Usage, codex.UsageUnavailableReason);

        if (dispatch?.WorkerProviderKind == ProviderKind.AnthropicClaudeCli && dispatch.ContextPackageReceipt is not null)
        {
            IReadOnlyList<string>? additionalRoots = dispatch.SandboxLowIntegrity
                ? [Path.Combine(dispatch.WorkingDirectory, ".mcg-sandbox")]
                : null;
            return ClaudeTranscriptUsage.Read(dispatch.ProviderSessionId, additionalRoots);
        }

        return new(null, "unsupported");
    }
}
