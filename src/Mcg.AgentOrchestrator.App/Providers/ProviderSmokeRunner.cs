using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Providers;

internal static class ProviderSmokeRunner
{
public const string DefaultTarget = "default";

public static string RunProviderSmoke(string target)
{
    var tester = new ProviderSmokeTester();
    var tested = 0;
    var evidence = new List<string>();

    foreach (var providerName in ResolveProviderSmokeTargets(target))
    {
        if (!TryCreateLiveProvider(providerName, out var provider, out var modelName, out var detail))
        {
            Console.WriteLine($"{providerName}: skipped - {detail}");
            evidence.Add($"{providerName}: skipped - {detail}");
            continue;
        }

        Console.WriteLine($"{providerName}: testing {modelName}...");
        var result = tester.RunAsync(provider).GetAwaiter().GetResult();
        PrintProviderSmokeResult(result, modelName);
        evidence.Add(FormatProviderSmokeEvidence(result, modelName));
        tested++;
    }

    if (tested == 0)
    {
        throw new InvalidOperationException(NoProviderSmokeRanMessage(target));
    }

    return string.Join(Environment.NewLine + Environment.NewLine, evidence);
}

public static async Task<ProviderSmokeReportDto> RunProviderSmokeReportAsync(string target)
{
    var tester = new ProviderSmokeTester();
    var results = new List<ProviderSmokeResultDto>();

    foreach (var providerName in ResolveProviderSmokeTargets(target))
    {
        if (!TryCreateLiveProvider(providerName, out var provider, out var modelName, out var detail))
        {
            results.Add(new ProviderSmokeResultDto(
                providerName,
                "skipped",
                null,
                detail,
                null,
                null,
                null,
                null));
            continue;
        }

        try
        {
            var result = await tester.RunAsync(provider);
            results.Add(new ProviderSmokeResultDto(
                result.ProviderName,
                "ok",
                modelName,
                "Provider smoke completed.",
                result.StopReason,
                result.Usage?.InputTokens,
                result.Usage?.OutputTokens,
                FormatProviderSmokeResponseText(result.ResponseText)));
        }
        catch (Exception ex)
        {
            results.Add(new ProviderSmokeResultDto(
                providerName,
                "failed",
                modelName,
                ex.Message,
                null,
                null,
                null,
                null));
        }
    }

    return new ProviderSmokeReportDto(
        target,
        results,
        results.Any(result => result.Status.Equals("ok", StringComparison.OrdinalIgnoreCase)),
        results.Any(result => !result.Status.Equals("skipped", StringComparison.OrdinalIgnoreCase)));
}

public static IReadOnlyList<string> ResolveProviderSmokeTargets(string target)
{
    return target.ToLowerInvariant() switch
    {
        "default" => ResolveDefaultProviderSmokeTargets(),
        "all" => ["OpenAI", "Anthropic", "Ollama"],
        "openai" => ["OpenAI"],
        "anthropic" => ["Anthropic"],
        "ollama" => ["Ollama"],
        _ => throw new ArgumentException("Usage: provider-smoke [openai|anthropic|ollama] [--confirm-paid-smoke] [task-number]; omit the target for local Ollama only; use provider-smoke all --confirm-all only for deliberate broad checks.")
    };
}

public static bool RequiresPaidConfirmation(string target)
{
    return ResolveProviderSmokeTargets(target)
        .Any(IsPaidProviderName);
}

public static bool IsPaidProviderName(string providerName)
{
    return providerName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) ||
        providerName.Equals("Anthropic", StringComparison.OrdinalIgnoreCase);
}

private static IReadOnlyList<string> ResolveDefaultProviderSmokeTargets()
{
    return ["Ollama"];
}

private static string NoProviderSmokeRanMessage(string target)
{
    return target.Equals(DefaultTarget, StringComparison.OrdinalIgnoreCase)
        ? "Provider smoke did not run. Local Ollama was not reachable; use provider-smoke openai --confirm-paid-smoke or provider-smoke anthropic --confirm-paid-smoke for an explicit paid smoke."
        : "Provider smoke did not run. Configure the selected provider, then retry.";
}

public static bool TryCreateLiveProvider(string providerName, out IModelProvider provider, out string modelName, out string detail)
{
    provider = null!;
    modelName = string.Empty;
    detail = string.Empty;

    if (providerName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            detail = "OPENAI_API_KEY is not set";
            return false;
        }

        modelName = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? ProviderModelDefaults.OpenAi;
        provider = new OpenAiResponsesModelProvider(ProviderHttpClientFactory.CreateOpenAiClient(), key, modelName);
        return true;
    }

    if (providerName.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
    {
        var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            detail = "ANTHROPIC_API_KEY is not set";
            return false;
        }

        modelName = Environment.GetEnvironmentVariable("ANTHROPIC_MODEL") ?? ProviderModelDefaults.Anthropic;
        provider = new AnthropicMessagesModelProvider(ProviderHttpClientFactory.CreateAnthropicClient(), key, modelName);
        return true;
    }

    if (providerName.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
    {
        var baseUrl = Environment.GetEnvironmentVariable("OLLAMA_BASE_URL") ?? "http://localhost:11434";
        modelName = Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? ProviderModelDefaults.Ollama;

        try
        {
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var check = probe.GetAsync($"{baseUrl.TrimEnd('/')}/api/version").GetAwaiter().GetResult();
            if (!check.IsSuccessStatusCode)
            {
                detail = $"Ollama not reachable at {baseUrl}";
                return false;
            }
        }
        catch
        {
            detail = $"Ollama not reachable at {baseUrl}";
            return false;
        }

        provider = new ChatCompletionsModelProvider(
            ProviderHttpClientFactory.CreateOllamaClient(baseUrl),
            modelName,
            "Ollama");
        return true;
    }

    detail = "unknown provider";
    return false;
}

public static void PrintProviderSmokeResult(ProviderSmokeResult result, string modelName)
{
    Console.WriteLine($"{result.ProviderName}: ok model={modelName}");
    Console.WriteLine($"Stop reason: {result.StopReason}");
    Console.WriteLine($"Usage: input={result.Usage?.InputTokens?.ToString() ?? "n/a"} output={result.Usage?.OutputTokens?.ToString() ?? "n/a"}");

    var text = result.ResponseText.Trim();
    if (text.Length > 500)
    {
        text = text[..500] + "...";
    }

    Console.WriteLine($"Response: {text}");
}

public static string FormatProviderSmokeEvidence(ProviderSmokeResult result, string modelName)
{
    return string.Join(
        Environment.NewLine,
        $"{result.ProviderName}: ok model={modelName}",
        $"Stop reason: {result.StopReason}",
        $"Usage: input={result.Usage?.InputTokens?.ToString() ?? "n/a"} output={result.Usage?.OutputTokens?.ToString() ?? "n/a"}",
        $"Response: {FormatProviderSmokeResponseText(result.ResponseText)}");
}

public static string FormatProviderSmokeResponseText(string text) =>
    OutputTextPreview.CreateTimeline(text.Trim()).Text;
}


