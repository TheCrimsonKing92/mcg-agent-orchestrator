using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record HermesAcpRequest(
    string PromptPath,
    string ExpectedPromptSha256,
    string WorkspaceRoot,
    string SandboxRoot,
    string ExpectedModel,
    string ExpectedProvider,
    AgentRole Role,
    string? HermesHome = null);

internal sealed record HermesAcpLaunchPlan(
    ProcessStartInfo StartInfo,
    byte[] PromptBytes,
    string PromptSha256,
    string HermesHome,
    string PinnedRelease,
    string PinnedCommit);

internal sealed record HermesAcpTerminalReceipt(
    string PromptSha256,
    string Model,
    string Provider,
    long InputTokens,
    long OutputTokens,
    bool Completed,
    int ExitCode,
    bool CancellationOrShutdownAcknowledged,
    bool JobExitConfirmed,
    string StandardErrorSha256,
    bool PermissionPolicyViolated,
    bool UnexpectedChild,
    string FinalOutput,
    string? SessionId = null,
    string? StopReason = null,
    string? PinnedRelease = null,
    string? PinnedCommit = null,
    string? Failure = null,
    bool SurvivorInventoryEmpty = false,
    HermesExecutableIdentityReceipt? ExecutableIdentity = null,
    HermesIdentityRefusal? IdentityRefusal = null);

internal sealed class HermesAcpAdapter
{
    public const string TerminalReceiptFileName = "hermes-acp-terminal-receipt.json";
    public const string PinnedRelease = "v2026.8.27";
    public const string PinnedTagObject = "fcebd62163497e77e5de00d26d2ed86cb4ef8761";
    public const string PinnedCommit = "5fc308a70719a83cccdbba4c0e39c23f5a8239d5";

    public HermesAcpLaunchPlan Prepare(HermesAcpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var workspaceRoot = RequireDirectory(request.WorkspaceRoot, "workspace");
        var sandboxRoot = RequireDirectory(request.SandboxRoot, "sandbox");
        var promptPath = Path.GetFullPath(request.PromptPath);
        if (!File.Exists(promptPath))
        {
            throw new FileNotFoundException("Hermes ACP prompt file was not found.", promptPath);
        }

        if (!IsWithin(promptPath, workspaceRoot) && !IsWithin(promptPath, sandboxRoot))
        {
            throw new InvalidOperationException("Hermes ACP prompt must be inside the assigned worktree or per-run sandbox state.");
        }

        var promptBytes = File.ReadAllBytes(promptPath);
        var promptSha256 = Convert.ToHexString(SHA256.HashData(promptBytes)).ToLowerInvariant();
        if (!promptSha256.Equals(request.ExpectedPromptSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Hermes ACP prompt digest mismatch: expected {request.ExpectedPromptSha256}, observed {promptSha256}.");
        }

        var hermesHome = ResolveHermesHome(request.HermesHome, sandboxRoot);
        var startInfo = new ProcessStartInfo
        {
            FileName = "hermes",
            WorkingDirectory = workspaceRoot,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--safe-mode");
        startInfo.ArgumentList.Add("acp");
        startInfo.Environment["HERMES_HOME"] = hermesHome;
        startInfo.Environment["HERMES_ACP_SKIP_CONFIGURED_MCP"] = "1";
        HarnessHookRootContract.Apply(startInfo);
        return new(startInfo, promptBytes, promptSha256, hermesHome, PinnedRelease, PinnedCommit);
    }

    public static void ValidateTerminalReceipt(
        HermesAcpRequest request,
        HermesAcpTerminalReceipt receipt,
        HermesPinnedIdentity? pin = null,
        HermesExecutableIdentityReceipt? expectedSameRunIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(receipt);
        if (!receipt.PromptSha256.Equals(request.ExpectedPromptSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Hermes ACP terminal receipt prompt digest did not match the requested prompt.");
        if (!receipt.Model.Equals(request.ExpectedModel, StringComparison.OrdinalIgnoreCase) ||
            !receipt.Provider.Equals(request.ExpectedProvider, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Hermes ACP terminal receipt reported a hidden model/provider fallback.");
        if (!receipt.Completed || receipt.InputTokens <= 0 || receipt.OutputTokens <= 0)
            throw new InvalidOperationException("Hermes ACP terminal receipt is missing completed non-zero usage evidence.");
        if (receipt.ExitCode != 0 || !receipt.CancellationOrShutdownAcknowledged ||
            !receipt.JobExitConfirmed || !receipt.SurvivorInventoryEmpty)
            throw new InvalidOperationException("Hermes ACP terminal receipt does not prove clean process completion and teardown.");
        if (receipt.StandardErrorSha256.Length != 64 || !receipt.StandardErrorSha256.All(Uri.IsHexDigit))
            throw new InvalidOperationException("Hermes ACP terminal receipt does not contain a valid stderr digest.");
        if (receipt.PermissionPolicyViolated)
            throw new InvalidOperationException("Hermes ACP permission policy was violated.");
        if (receipt.UnexpectedChild)
            throw new InvalidOperationException("Hermes ACP launched an unexpected child process.");
        if (expectedSameRunIdentity is not null && !ReferenceEquals(receipt.ExecutableIdentity, expectedSameRunIdentity))
            throw new HermesIdentityException(HermesIdentityRefusal.ReceiptStale, "terminal receipt did not retain the identity receipt created by this lifecycle run");
        GitHermesExecutableIdentityVerifier.ValidateReceipt(
            receipt.ExecutableIdentity,
            pin: pin,
            enforceFreshness: expectedSameRunIdentity is null);
        if (!WorkerResultParser.TryParseFields(receipt.FinalOutput, out _, out var error))
            throw new InvalidOperationException($"Hermes ACP final output did not contain an authoritative WORKER_RESULT: {error}");
    }

    public static void PersistTerminalReceipt(
        HermesAcpRequest request,
        string receiptPath,
        HermesAcpTerminalReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(receipt);
        var fullPath = Path.GetFullPath(receiptPath);
        var workspaceRoot = RequireDirectory(request.WorkspaceRoot, "workspace");
        var sandboxRoot = RequireDirectory(request.SandboxRoot, "sandbox");
        if (!IsWithin(fullPath, workspaceRoot) && !IsWithin(fullPath, sandboxRoot))
        {
            throw new InvalidOperationException(
                "Hermes ACP terminal receipt must be inside the assigned worktree or per-run sandbox state.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = fullPath + $".{Guid.NewGuid():N}.tmp";
        var json = JsonSerializer.Serialize(
            receipt,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        File.WriteAllText(temporaryPath, json + Environment.NewLine);
        File.Move(temporaryPath, fullPath, overwrite: true);
    }

    private static string RequireDirectory(string path, string name)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(Path.GetFullPath(path)))
            throw new DirectoryNotFoundException($"Hermes ACP {name} directory does not exist: '{path}'.");
        return Path.GetFullPath(path);
    }

    private static string ResolveHermesHome(string? requestedHome, string sandboxRoot)
    {
        if (string.IsNullOrWhiteSpace(requestedHome))
        {
            var created = Path.Combine(sandboxRoot, $"hermes-home-{Guid.NewGuid():N}");
            Directory.CreateDirectory(created);
            return created;
        }

        var fullPath = Path.GetFullPath(requestedHome);
        if (!IsWithin(fullPath, sandboxRoot))
        {
            throw new InvalidOperationException("Hermes ACP HERMES_HOME must remain inside the per-run sandbox state.");
        }

        Directory.CreateDirectory(fullPath);
        return fullPath;
    }

    private static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}
