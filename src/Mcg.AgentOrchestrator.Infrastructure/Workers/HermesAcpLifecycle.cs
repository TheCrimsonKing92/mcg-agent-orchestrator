using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal interface IHermesAcpProcess : IDisposable
{
    TextWriter StandardInput { get; }
    TextReader StandardOutput { get; }
    TextReader StandardError { get; }
    int ExitCode { get; }
    void CompleteInput();
    void Kill();
    Task WaitForExitAsync(CancellationToken cancellationToken);
}

internal interface IHermesAcpProcessLauncher
{
    IHermesAcpProcess Start(ProcessStartInfo startInfo);
}

internal sealed class HermesAcpProcessLauncher : IHermesAcpProcessLauncher
{
    public IHermesAcpProcess Start(ProcessStartInfo startInfo) =>
        new HermesAcpProcess(ProcessTreeGuiSuppression.Start(startInfo));

    private sealed class HermesAcpProcess(Process process) : IHermesAcpProcess
    {
        public TextWriter StandardInput => process.StandardInput;
        public TextReader StandardOutput => process.StandardOutput;
        public TextReader StandardError => process.StandardError;
        public int ExitCode => process.ExitCode;
        public void CompleteInput() => process.StandardInput.Close();
        public void Kill()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // The dispatch/trial job remains the authoritative final reaper.
            }
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken) =>
            process.WaitForExitAsync(cancellationToken);

        public void Dispose() => process.Dispose();
    }
}

internal sealed class HermesAcpLifecycle
{
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);
    private readonly HermesAcpAdapter _adapter;
    private readonly IHermesAcpProcessLauncher _launcher;

    public HermesAcpLifecycle(
        HermesAcpAdapter? adapter = null,
        IHermesAcpProcessLauncher? launcher = null)
    {
        _adapter = adapter ?? new HermesAcpAdapter();
        _launcher = launcher ?? new HermesAcpProcessLauncher();
    }

    public async Task<HermesAcpTerminalReceipt> RunAsync(
        HermesAcpRequest request,
        string receiptPath,
        TextWriter progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);
        var plan = _adapter.Prepare(request);
        var stderr = string.Empty;
        var finalOutput = string.Empty;
        var observedModel = string.Empty;
        var observedProvider = string.Empty;
        var sessionId = string.Empty;
        var stopReason = string.Empty;
        long inputTokens = 0;
        long outputTokens = 0;
        var exitCode = -1;
        var shutdownConfirmed = false;
        var permissionPolicyViolated = false;
        var unexpectedChild = false;
        IHermesAcpProcess? process = null;
        Task<string>? stderrDrain = null;

        try
        {
            await ValidatePinnedVersionAsync(plan, cancellationToken).ConfigureAwait(false);
            process = _launcher.Start(plan.StartInfo);
            stderrDrain = process.StandardError.ReadToEndAsync(cancellationToken);
            var rpc = new HermesAcpJsonRpcClient(process.StandardInput, process.StandardOutput, progress);

            _ = await rpc.CallAsync(
                "initialize",
                new
                {
                    protocolVersion = 1,
                    clientCapabilities = new
                    {
                        fs = new { readTextFile = false, writeTextFile = false },
                        terminal = false
                    },
                    clientInfo = new { name = "mcg-agent-orchestrator", version = "1" }
                },
                cancellationToken).ConfigureAwait(false);

            var session = await rpc.CallAsync(
                "session/new",
                new { cwd = Path.GetFullPath(request.WorkspaceRoot), mcpServers = Array.Empty<object>() },
                cancellationToken).ConfigureAwait(false);
            sessionId = RequireString(session, "sessionId", "Hermes ACP session/new response");
            var currentModelId = RequireNestedString(session, "models", "currentModelId", "Hermes ACP session/new response");
            (observedProvider, observedModel) = SplitModelIdentity(currentModelId);
            if (!observedModel.Equals(request.ExpectedModel, StringComparison.OrdinalIgnoreCase) ||
                !observedProvider.Equals(request.ExpectedProvider, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Hermes ACP selected hidden model/provider fallback '{currentModelId}', expected '{request.ExpectedProvider}:{request.ExpectedModel}'.");
            }

            var prompt = Encoding.UTF8.GetString(plan.PromptBytes);
            var promptResponse = await rpc.CallAsync(
                "session/prompt",
                new
                {
                    sessionId,
                    prompt = new[] { new { type = "text", text = prompt } }
                },
                cancellationToken).ConfigureAwait(false);
            stopReason = RequireString(promptResponse, "stopReason", "Hermes ACP session/prompt response");
            finalOutput = rpc.FinalOutput;
            permissionPolicyViolated = rpc.PermissionPolicyViolated;
            unexpectedChild = rpc.UnexpectedChild;
            if (promptResponse.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                inputTokens = ReadInt64(usage, "inputTokens");
                outputTokens = ReadInt64(usage, "outputTokens");
            }

            process.CompleteInput();
            shutdownConfirmed = await WaitForExitAsync(process, ShutdownTimeout, cancellationToken).ConfigureAwait(false);
            if (!shutdownConfirmed)
            {
                process.Kill();
                throw new TimeoutException("Hermes ACP server did not exit after stdin shutdown.");
            }

            exitCode = process.ExitCode;
            stderr = await stderrDrain.WaitAsync(ShutdownTimeout, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                await progress.WriteAsync(stderr).ConfigureAwait(false);
                await progress.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var receipt = CreateReceipt(
                request,
                finalOutput,
                observedModel,
                observedProvider,
                inputTokens,
                outputTokens,
                stopReason.Equals("end_turn", StringComparison.OrdinalIgnoreCase) && exitCode == 0,
                exitCode,
                shutdownConfirmed,
                stderr,
                permissionPolicyViolated,
                unexpectedChild,
                sessionId,
                stopReason,
                failure: null);
            HermesAcpAdapter.PersistTerminalReceipt(request, receiptPath, receipt);
            HermesAcpAdapter.ValidateTerminalReceipt(request, receipt);
            return receipt;
        }
        catch (Exception ex)
        {
            if (process is not null)
            {
                process.Kill();
                try
                {
                    shutdownConfirmed = await WaitForExitAsync(
                        process,
                        ShutdownTimeout,
                        CancellationToken.None).ConfigureAwait(false);
                    if (shutdownConfirmed)
                    {
                        exitCode = process.ExitCode;
                    }

                    if (stderrDrain is not null)
                    {
                        stderr = await stderrDrain.WaitAsync(ShutdownTimeout).ConfigureAwait(false);
                    }
                }
                catch
                {
                    shutdownConfirmed = false;
                }
            }

            var failedReceipt = CreateReceipt(
                request,
                finalOutput,
                observedModel,
                observedProvider,
                inputTokens,
                outputTokens,
                completed: false,
                exitCode,
                shutdownConfirmed,
                stderr,
                permissionPolicyViolated,
                unexpectedChild,
                sessionId,
                stopReason,
                ex.Message);
            HermesAcpAdapter.PersistTerminalReceipt(request, receiptPath, failedReceipt);
            throw;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private async Task ValidatePinnedVersionAsync(
        HermesAcpLaunchPlan plan,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = plan.StartInfo.FileName,
            WorkingDirectory = plan.StartInfo.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--version");
        startInfo.Environment["HERMES_HOME"] = plan.HermesHome;
        startInfo.Environment["HERMES_ACP_SKIP_CONFIGURED_MCP"] = "1";

        using var process = _launcher.Start(startInfo);
        process.CompleteInput();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        if (!await WaitForExitAsync(process, VersionTimeout, cancellationToken).ConfigureAwait(false))
        {
            process.Kill();
            throw new TimeoutException("Hermes version preflight did not exit within 30 seconds.");
        }

        var versionOutput = (await stdout.ConfigureAwait(false)) + Environment.NewLine +
            (await stderr.ConfigureAwait(false));
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Hermes version preflight exited {process.ExitCode}: {versionOutput.Trim()}");
        }

        HermesAcpAdapter.ValidateVersionOutput(versionOutput);
    }

    private static async Task<bool> WaitForExitAsync(
        IHermesAcpProcess process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static HermesAcpTerminalReceipt CreateReceipt(
        HermesAcpRequest request,
        string finalOutput,
        string model,
        string provider,
        long inputTokens,
        long outputTokens,
        bool completed,
        int exitCode,
        bool shutdownConfirmed,
        string stderr,
        bool permissionPolicyViolated,
        bool unexpectedChild,
        string sessionId,
        string stopReason,
        string? failure) =>
        new(
            request.ExpectedPromptSha256,
            model,
            provider,
            inputTokens,
            outputTokens,
            completed,
            exitCode,
            shutdownConfirmed,
            shutdownConfirmed,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stderr))).ToLowerInvariant(),
            permissionPolicyViolated,
            unexpectedChild,
            finalOutput,
            sessionId,
            stopReason,
            HermesAcpAdapter.PinnedRelease,
            HermesAcpAdapter.PinnedCommit,
            failure);

    private static string RequireString(JsonElement element, string property, string source)
    {
        if (!element.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidOperationException($"{source} did not contain required string '{property}'.");
        }

        return value.GetString()!;
    }

    private static string RequireNestedString(
        JsonElement element,
        string parent,
        string property,
        string source)
    {
        if (!element.TryGetProperty(parent, out var nested) || nested.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"{source} did not contain required object '{parent}'.");
        }

        return RequireString(nested, property, source);
    }

    private static long ReadInt64(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt64(out var parsed) ? parsed : 0;

    private static (string Provider, string Model) SplitModelIdentity(string currentModelId)
    {
        var separator = currentModelId.LastIndexOf(':');
        if (separator <= 0 || separator == currentModelId.Length - 1)
        {
            throw new InvalidOperationException(
                $"Hermes ACP currentModelId '{currentModelId}' did not identify both provider and model.");
        }

        return (currentModelId[..separator], currentModelId[(separator + 1)..]);
    }
}

internal sealed class HermesAcpJsonRpcClient(
    TextWriter input,
    TextReader output,
    TextWriter progress)
{
    private readonly StringBuilder _finalOutput = new();
    private int _nextId;

    public string FinalOutput => _finalOutput.ToString();
    public bool PermissionPolicyViolated { get; private set; }
    public bool UnexpectedChild { get; private set; }

    public async Task<JsonElement> CallAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        await WriteAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, cancellationToken)
            .ConfigureAwait(false);

        while (true)
        {
            var line = await output.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                throw new EndOfStreamException($"Hermes ACP stdout closed while waiting for '{method}' response {id}.");
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            var message = document.RootElement;
            if (message.TryGetProperty("method", out var incomingMethod) &&
                incomingMethod.ValueKind == JsonValueKind.String)
            {
                await HandleIncomingMethodAsync(message, incomingMethod.GetString()!, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (!message.TryGetProperty("id", out var responseId) ||
                !responseId.TryGetInt32(out var responseNumber) ||
                responseNumber != id)
            {
                continue;
            }

            if (message.TryGetProperty("error", out var error))
            {
                throw new InvalidOperationException($"Hermes ACP '{method}' failed: {error.GetRawText()}");
            }

            if (!message.TryGetProperty("result", out var result))
            {
                throw new InvalidOperationException($"Hermes ACP '{method}' response did not contain a result.");
            }

            return result.Clone();
        }
    }

    private async Task HandleIncomingMethodAsync(
        JsonElement message,
        string method,
        CancellationToken cancellationToken)
    {
        if (method.Equals("session/update", StringComparison.Ordinal))
        {
            CaptureSessionUpdate(message);
            return;
        }

        if (!message.TryGetProperty("id", out var requestId))
        {
            return;
        }

        if (method.Equals("session/request_permission", StringComparison.Ordinal))
        {
            var optionId = FindRejectOption(message);
            object result = optionId is null
                ? new { outcome = new { outcome = "cancelled" } }
                : new { outcome = new { outcome = "selected", optionId } };
            await WriteAsync(new { jsonrpc = "2.0", id = requestId.Clone(), result }, cancellationToken)
                .ConfigureAwait(false);
            await progress.WriteLineAsync("[hermes-acp] denied permission request.").ConfigureAwait(false);
            return;
        }

        PermissionPolicyViolated = true;
        await WriteAsync(
            new
            {
                jsonrpc = "2.0",
                id = requestId.Clone(),
                error = new { code = -32601, message = $"Unsupported Hermes ACP client method: {method}" }
            },
            cancellationToken).ConfigureAwait(false);
    }

    private void CaptureSessionUpdate(JsonElement message)
    {
        if (!message.TryGetProperty("params", out var parameters) ||
            !parameters.TryGetProperty("update", out var update) ||
            !update.TryGetProperty("sessionUpdate", out var updateType) ||
            updateType.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var kind = updateType.GetString();
        if (kind == "agent_message_chunk" &&
            update.TryGetProperty("content", out var content) &&
            content.TryGetProperty("text", out var text) &&
            text.ValueKind == JsonValueKind.String)
        {
            _finalOutput.Append(text.GetString());
        }

        if ((kind == "tool_call" || kind == "tool_call_update") &&
            update.GetRawText().Contains("subagent", StringComparison.OrdinalIgnoreCase))
        {
            UnexpectedChild = true;
        }
    }

    private static string? FindRejectOption(JsonElement message)
    {
        if (!message.TryGetProperty("params", out var parameters) ||
            !parameters.TryGetProperty("options", out var options) ||
            options.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var option in options.EnumerateArray())
        {
            var kind = option.TryGetProperty("kind", out var kindElement) ? kindElement.GetString() : null;
            if (kind is not ("reject_once" or "reject_always"))
            {
                continue;
            }

            return option.TryGetProperty("optionId", out var id) ? id.GetString() : null;
        }

        return null;
    }

    private async Task WriteAsync(object message, CancellationToken cancellationToken)
    {
        await input.WriteLineAsync(JsonSerializer.Serialize(message)).ConfigureAwait(false);
        await input.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
