using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Hosting;

internal sealed record DashboardUrlPrefixes(string BindUrlPrefix, string? PublicUrlPrefix);

internal static class DashboardHost
{
public const int DefaultHostedSourceSurveyMaxFiles = 8;

public static int RunPrototypeUi(
    IReadOnlyList<string> parts,
    IModelProviderRegistry providers,
    AgentCatalog? agentFallback = null,
    string? tenantName = null)
{
    var hostArgs = ParseDashboardHostArgs(parts, "prototype-ui", defaultOpenBrowser: true);
    var prototypeWorkspacePath = PrototypeWorkspaceSeeder.Create(Environment.CurrentDirectory, agentFallback);
    var executionDirectory = Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_REPOSITORY_ROOT");
    var prototypeWorkspace = OrchestratorWorkspace.ForDirectory(
        prototypeWorkspacePath,
        string.IsNullOrWhiteSpace(executionDirectory) ? Environment.CurrentDirectory : executionDirectory,
        tenantName);

    Console.WriteLine($"Prototype state workspace: {prototypeWorkspace.RootDirectory}");
    Console.WriteLine($"Prototype execution directory: {prototypeWorkspace.ExecutionDirectory}");
    Console.WriteLine("Prototype state is persistent and isolated from the real .orchestrator directory.");

    RunDashboardHostAsync(prototypeWorkspace, providers, hostArgs, agentFallback)
        .GetAwaiter()
        .GetResult();
    return 0;
}

public static DashboardCommandArgs ParseDashboardArgs(IReadOnlyList<string> parts)
{
    var defaultPath = Path.Combine(Environment.CurrentDirectory, ".orchestrator", "dashboard.html");
    var path = defaultPath;
    var hasPath = false;
    int? refreshSeconds = null;

    for (var index = 1; index < parts.Count; index++)
    {
        var part = parts[index];
        if (part.Equals("--refresh", StringComparison.OrdinalIgnoreCase))
        {
            if (index + 1 >= parts.Count)
            {
                throw new ArgumentException("Usage: dashboard [path] [--refresh seconds]");
            }

            if (!int.TryParse(parts[index + 1], out var parsed) || parsed < 0)
            {
                throw new ArgumentException("Dashboard refresh seconds must be zero or a positive integer.");
            }

            refreshSeconds = parsed == 0 ? null : parsed;
            index++;
            continue;
        }

        if (hasPath)
        {
            throw new ArgumentException("Usage: dashboard [path] [--refresh seconds]");
        }

        path = part;
        hasPath = true;
    }

    return new DashboardCommandArgs(path, new DashboardRenderOptions(refreshSeconds));
}

public static DashboardHostArgs ParseDashboardHostArgs(IReadOnlyList<string> parts, string commandName, bool defaultOpenBrowser)
{
    var hosted = IsHostedDashboardCommand(commandName);
    var enableOperatorControls = !commandName.Equals("simple-hosted-dashboard", StringComparison.OrdinalIgnoreCase);
    var usage = $"Usage: {commandName} [port|url] [--refresh seconds] [--open] [--no-open]";
    var preferredPort = hosted
        ? GetHostedDashboardPortFromEnvironment() ?? 5087
        : 5087;
    var urlPrefix = hosted
        ? SelectAvailableHostedUrlPrefix(preferredPort)
        : SelectAvailableLocalUrlPrefix(preferredPort);
    string? publicUrlPrefix = null;
    var hasUrl = false;
    int? refreshSeconds = 5;
    var openBrowser = defaultOpenBrowser;

    for (var index = 1; index < parts.Count; index++)
    {
        var part = parts[index];
        if (part.Equals("--no-open", StringComparison.OrdinalIgnoreCase))
        {
            openBrowser = false;
            continue;
        }

        if (part.Equals("--open", StringComparison.OrdinalIgnoreCase))
        {
            openBrowser = true;
            continue;
        }

        if (part.Equals("--refresh", StringComparison.OrdinalIgnoreCase))
        {
            if (index + 1 >= parts.Count)
            {
                throw new ArgumentException(usage);
            }

            if (!int.TryParse(parts[index + 1], out var parsed) || parsed < 1)
            {
                throw new ArgumentException("Dashboard refresh seconds must be a positive integer.");
            }

            refreshSeconds = parsed;
            index++;
            continue;
        }

        if (hasUrl)
        {
            throw new ArgumentException(usage);
        }

        var parsedUrl = NormalizeUrlPrefix(part, hosted, preferredPort);
        urlPrefix = parsedUrl.BindUrlPrefix;
        publicUrlPrefix = parsedUrl.PublicUrlPrefix;
        hasUrl = true;
    }

    return new DashboardHostArgs(urlPrefix, refreshSeconds, openBrowser, commandName, publicUrlPrefix, enableOperatorControls);
}

public static int? GetHostedDashboardPortFromEnvironment()
{
    return TryParsePort(Environment.GetEnvironmentVariable("PORT"))
        ?? TryParseFirstPort(Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS"))
        ?? TryParseFirstPort(Environment.GetEnvironmentVariable("HTTP_PORTS"))
        ?? TryParseFirstUrlPort(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"));
}

public static string SelectAvailableLocalUrlPrefix(int preferredPort)
{
    for (var port = preferredPort; port < preferredPort + 100; port++)
    {
        if (CanBindLoopbackPort(port))
        {
            return $"http://localhost:{port}/";
        }
    }

    return $"http://localhost:{preferredPort}/";
}

public static string SelectAvailableHostedUrlPrefix(int preferredPort)
{
    for (var port = preferredPort; port < preferredPort + 100; port++)
    {
        if (CanBindAnyIPv4Port(port))
        {
            return $"http://0.0.0.0:{port}/";
        }
    }

    return $"http://0.0.0.0:{preferredPort}/";
}

public static string EnsureTrailingSlash(string value)
{
    return value.EndsWith("/", StringComparison.Ordinal) ? value : value + "/";
}

public static DashboardUrlPrefixes NormalizeUrlPrefix(string value, bool hosted, int? hostedBindPort = null)
{
    var trimmed = value.Trim();
    if (string.IsNullOrWhiteSpace(trimmed))
    {
        throw new ArgumentException("Dashboard URL cannot be empty.");
    }

    if (int.TryParse(trimmed, out var port))
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentException("Dashboard port must be between 1 and 65535.");
        }

        return new DashboardUrlPrefixes(
            hosted ? $"http://0.0.0.0:{port}/" : $"http://localhost:{port}/",
            null);
    }

    if (trimmed.StartsWith("-", StringComparison.Ordinal))
    {
        throw new ArgumentException($"Unknown dashboard option '{trimmed}'.");
    }

    if (!trimmed.Contains("://", StringComparison.Ordinal))
    {
        trimmed = "http://" + trimmed;
    }

    if (!Uri.TryCreate(EnsureTrailingSlash(trimmed), UriKind.Absolute, out var uri) ||
        string.IsNullOrWhiteSpace(uri.Host))
    {
        throw new ArgumentException($"Dashboard URL '{value}' is invalid.");
    }

    var builder = new UriBuilder(uri)
    {
        Path = "/",
        Query = string.Empty,
        Fragment = string.Empty
    };

    string? publicUrlPrefix = null;
    var bindPort = hosted && uri.IsDefaultPort
        ? hostedBindPort
        : uri.Port;

    if (hosted && IsWildcardHost(uri.Host))
    {
        builder.Host = "0.0.0.0";
        builder.Scheme = Uri.UriSchemeHttp;
        if (bindPort is > 0)
        {
            builder.Port = bindPort.Value;
        }
    }
    else if (hosted)
    {
        publicUrlPrefix = EnsureTrailingSlash(builder.Uri.GetLeftPart(UriPartial.Authority));
        builder.Host = "0.0.0.0";
        builder.Scheme = Uri.UriSchemeHttp;
        if (bindPort is > 0)
        {
            builder.Port = bindPort.Value;
        }
    }

    return new DashboardUrlPrefixes(
        EnsureTrailingSlash(builder.Uri.GetLeftPart(UriPartial.Authority)),
        publicUrlPrefix);
}

private static bool CanBindLoopbackPort(int port)
{
    try
    {
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        return true;
    }
    catch (SocketException)
    {
        return false;
    }
}

private static int? TryParseFirstPort(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    foreach (var candidate in value.Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var port = TryParsePort(candidate);
        if (port is not null)
        {
            return port;
        }
    }

    return null;
}

private static int? TryParseFirstUrlPort(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    foreach (var candidate in value.Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Port is >= 1 and <= 65535)
        {
            return uri.Port;
        }
    }

    return null;
}

private static int? TryParsePort(string? value)
{
    return int.TryParse(value, out var port) && port is >= 1 and <= 65535
        ? port
        : null;
}

private static bool CanBindAnyIPv4Port(int port)
{
    try
    {
        using var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        return true;
    }
    catch (SocketException)
    {
        return false;
    }
}

private static bool IsLoopbackHost(string host)
{
    return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("::1", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("[::1]", StringComparison.OrdinalIgnoreCase);
}

private static bool IsLoopbackUrlPrefix(string urlPrefix)
{
    return Uri.TryCreate(urlPrefix, UriKind.Absolute, out var uri) &&
        IsLoopbackHost(uri.Host);
}

public static async Task RunDashboardHostAsync(
    OrchestratorWorkspace workspace,
    IModelProviderRegistry providers,
    DashboardHostArgs args,
    AgentCatalog? agentCatalogFallback = null)
{
    var repository = new FileOrchestratorStateRepository(workspace.StatePath);
    var builder = WebApplication.CreateBuilder();
    builder.WebHost.UseUrls(args.UrlPrefix);

    var app = builder.Build();
    app.MapDashboardEndpoints(repository, workspace, providers, args, agentCatalogFallback);

    if (args.OpenBrowser)
    {
        app.Lifetime.ApplicationStarted.Register(() => OpenDashboardInBrowser(GetDashboardPageUrl(GetBrowserUrl(args))));
    }

    var browserUrl = GetBrowserUrl(args);
    var dashboardPageUrl = GetDashboardPageUrl(browserUrl);
    Console.WriteLine($"Dashboard bind URL: {args.UrlPrefix}");
    Console.WriteLine($"Dashboard mode: {(args.EnableOperatorControls ? "operator controls enabled" : "read-only simple hosted view")}");
    Console.WriteLine($"Dashboard state workspace: {workspace.RootDirectory}");
    Console.WriteLine($"Dashboard execution directory: {workspace.ExecutionDirectory}");
    Console.WriteLine($"Dashboard page: {dashboardPageUrl}");
    Console.WriteLine($"Dashboard restart command: {BuildDashboardRestartCommand(args, workspace)}");
    var hostedUrlPrefixes = GetHostedUrlPrefixes(args);
    foreach (var hostedUrl in hostedUrlPrefixes.Select(GetDashboardPageUrl))
    {
        Console.WriteLine($"Hosted dashboard page: {hostedUrl}");
    }

    Console.WriteLine($"Source survey: {GetSourceSurveyUrl(browserUrl)}");
    foreach (var hostedUrl in hostedUrlPrefixes)
    {
        Console.WriteLine($"Hosted source survey: {GetSourceSurveyUrl(hostedUrl)}");
    }

    var hostedAccessNote = GetHostedAccessNote(args.UrlPrefix, hostedUrlPrefixes);
    if (!string.IsNullOrWhiteSpace(hostedAccessNote))
    {
        Console.WriteLine($"Hosted access note: {hostedAccessNote}");
    }

    Console.WriteLine("Press Ctrl+C to stop.");
    await app.RunAsync();
}

public static void OpenDashboardInBrowser(string url)
{
    try
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        Console.WriteLine($"Opened browser: {url}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Could not open browser automatically: {ex.Message}");
    }
}

public static string GetBrowserUrl(string urlPrefix)
{
    if (!Uri.TryCreate(urlPrefix, UriKind.Absolute, out var uri))
    {
        return urlPrefix;
    }

    if (uri.Host.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("*", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("+", StringComparison.OrdinalIgnoreCase))
    {
        var builder = new UriBuilder(uri)
        {
            Host = "127.0.0.1"
        };
        return builder.Uri.ToString();
    }

    if (uri.Host.Equals("::", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("[::]", StringComparison.OrdinalIgnoreCase))
    {
        var builder = new UriBuilder(uri)
        {
            Host = "[::1]"
        };
        return builder.Uri.ToString();
    }

    return urlPrefix;
}

public static string GetBrowserUrl(DashboardHostArgs args)
{
    return string.IsNullOrWhiteSpace(args.PublicUrlPrefix)
        ? GetBrowserUrl(args.UrlPrefix)
        : args.PublicUrlPrefix;
}

public static string GetRestartUrl(DashboardHostArgs args)
{
    if (!IsHostedDashboardCommand(args.CommandName))
    {
        return args.PublicUrlPrefix ?? args.UrlPrefix;
    }

    if (!string.IsNullOrWhiteSpace(args.PublicUrlPrefix) &&
        !IsLoopbackUrlPrefix(args.PublicUrlPrefix))
    {
        return args.PublicUrlPrefix;
    }

    if (Uri.TryCreate(args.UrlPrefix, UriKind.Absolute, out var uri) &&
        IsWildcardHost(uri.Host) &&
        uri.Port > 0)
    {
        return uri.Port.ToString(CultureInfo.InvariantCulture);
    }

    return args.UrlPrefix;
}

public static string GetDashboardPageUrl(string urlPrefix)
{
    return new Uri(new Uri(EnsureTrailingSlash(urlPrefix)), "dashboard").ToString();
}

public static string GetSourceSurveyUrl(string urlPrefix)
{
    return new Uri(
        new Uri(EnsureTrailingSlash(urlPrefix)),
        $"api/source-survey?max={DefaultHostedSourceSurveyMaxFiles}").ToString();
}

public static List<string> GetHostedDashboardPageUrls(string urlPrefix)
{
    return GetHostedUrlPrefixes(urlPrefix)
        .Select(GetDashboardPageUrl)
        .ToList();
}

public static List<string> GetHostedUrlPrefixes(string urlPrefix)
{
    if (!Uri.TryCreate(urlPrefix, UriKind.Absolute, out var uri) ||
        !IsWildcardHost(uri.Host))
    {
        return [];
    }

    var urls = new List<string>();
    IPAddress[] addresses;
    try
    {
        addresses = Dns.GetHostEntry(Dns.GetHostName()).AddressList;
    }
    catch (SocketException)
    {
        addresses = [];
    }

    urls.AddRange(addresses
        .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
        .Select(address =>
        {
            var builder = new UriBuilder(uri)
            {
                Host = address.ToString()
            };
            return EnsureTrailingSlash(builder.Uri.ToString());
        }));

    var hostName = GetMachineHostName();
    if (!string.IsNullOrWhiteSpace(hostName) && !IsLoopbackHost(hostName))
    {
        var builder = new UriBuilder(uri)
        {
            Host = hostName
        };
        urls.Add(EnsureTrailingSlash(builder.Uri.ToString()));
    }

    return urls
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToList();
}

public static List<string> GetHostedUrlPrefixes(DashboardHostArgs args)
{
    return string.IsNullOrWhiteSpace(args.PublicUrlPrefix) ||
        IsLoopbackUrlPrefix(args.PublicUrlPrefix)
        ? GetHostedUrlPrefixes(args.UrlPrefix)
        : [args.PublicUrlPrefix];
}

public static string GetHostedAccessNote(string urlPrefix, IReadOnlyList<string> hostedUrlPrefixes)
{
    if (!Uri.TryCreate(urlPrefix, UriKind.Absolute, out var uri) ||
        !IsWildcardHost(uri.Host))
    {
        return "Dashboard is bound to a specific host; use the browser URL shown by the dashboard host.";
    }

    if (hostedUrlPrefixes.Count > 0)
    {
        return "Use a hosted dashboard URL from the list on another machine that can reach this host and port.";
    }

    return "Dashboard is listening on all IPv4 interfaces, but no non-loopback address was discovered. Use this machine's LAN or VPN IP with the same port.";
}

public static string BuildDashboardRestartCommand(DashboardHostArgs args)
{
    var refresh = args.AutoRefreshSeconds is > 0
        ? $" --refresh {args.AutoRefreshSeconds.Value}"
        : string.Empty;
    var dashboardUrl = GetRestartUrl(args);
    return $".\\mcg-orchestrator.cmd {args.CommandName} {dashboardUrl}{refresh} --no-open";
}

public static string BuildDashboardRestartCommand(DashboardHostArgs args, OrchestratorWorkspace workspace)
{
    var command = BuildDashboardRestartCommand(args);
    return workspace.IsTenantScoped
        ? command + $" --tenant {workspace.TenantName}"
        : command;
}

private static string? GetMachineHostName()
{
    try
    {
        return Dns.GetHostName();
    }
    catch (SocketException)
    {
        return null;
    }
}

private static bool IsWildcardHost(string host)
{
    return host.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("*", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("+", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("::", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("[::]", StringComparison.OrdinalIgnoreCase);
}

private static bool IsHostedDashboardCommand(string commandName)
{
    return commandName.Equals("hosted-dashboard", StringComparison.OrdinalIgnoreCase) ||
        commandName.Equals("simple-hosted-dashboard", StringComparison.OrdinalIgnoreCase);
}

}


