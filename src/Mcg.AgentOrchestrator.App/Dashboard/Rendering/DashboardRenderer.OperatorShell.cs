using System.Net;
using System.Text;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Rendering;

public static partial class DashboardRenderer
{
    private static void RenderCreateGoalForm(StringBuilder html)
    {
        html.AppendLine("<section>");
        html.AppendLine("<h2>Create Goal</h2>");
        html.AppendLine("<form class=\"controls compact\" data-action=\"/api/goals\">");
        html.AppendLine("<div class=\"field\"><label for=\"new-goal\">Goal</label><input id=\"new-goal\" class=\"wide\" name=\"objective\" required></div>");
        html.AppendLine("<div class=\"field\"><label for=\"new-goal-workflow\">Workflow</label><select id=\"new-goal-workflow\" name=\"workflow\"><option value=\"simple\">Simple task</option><option value=\"sdlc\">Full SDLC workflow</option></select></div>");
        html.AppendLine("<input type=\"hidden\" name=\"autoHandoff\" value=\"false\">");
        html.AppendLine("<input type=\"hidden\" name=\"confirmAutoHandoff\" value=\"true\">");
        html.AppendLine("<label class=\"checkrow\" for=\"new-goal-auto-handoff\"><input id=\"new-goal-auto-handoff\" type=\"checkbox\" name=\"autoHandoff\" value=\"true\">Automatically start subscription handoff</label>");
        html.AppendLine("<button class=\"primary\" type=\"submit\">Create Goal</button>");
        html.AppendLine("</form>");
        html.AppendLine("</section>");
    }

    private static void RenderSystemDiagnostics(
        StringBuilder html,
        DashboardWorkspaceContext? workspace,
        IReadOnlyList<DashboardContinuationStatusDto> continuationWatches)
    {
        html.AppendLine("<section>");
        html.AppendLine("<h2>System</h2>");
        if (workspace is not null)
        {
            var workspaceLabel = GetWorkspaceLabel(workspace);
            html.AppendLine("<div class=\"workspace-context\">");
            html.AppendLine($"<strong>{workspaceLabel}</strong>");
            html.AppendLine("<dl>");
            html.AppendLine($"<dt>State workspace</dt><dd><code>{Encode(workspace.RootDirectory)}</code></dd>");
            html.AppendLine($"<dt>Execution directory</dt><dd><code>{Encode(workspace.ExecutionDirectory)}</code></dd>");
            html.AppendLine($"<dt>State</dt><dd><code>{Encode(workspace.StatePath)}</code></dd>");
            html.AppendLine($"<dt>Prompts</dt><dd><code>{Encode(workspace.PromptDirectory)}</code></dd>");
            html.AppendLine($"<dt>Logs</dt><dd><code>{Encode(workspace.LogDirectory)}</code></dd>");
            html.AppendLine($"<dt>Workers</dt><dd><code>{Encode(workspace.WorkerProfilePath)}</code></dd>");
            html.AppendLine($"<dt>Agents</dt><dd><code>{Encode(workspace.AgentCatalogPath)}</code></dd>");
            html.AppendLine($"<dt>Dashboard PID</dt><dd><code>{workspace.DashboardProcessId}</code> <span class=\"meta\">Stop this known process before full build/test if Windows reports apphost or DLL locks.</span></dd>");
            if (!string.IsNullOrWhiteSpace(workspace.DashboardBindUrl))
            {
                html.AppendLine($"<dt>Bind URL</dt><dd><code>{Encode(workspace.DashboardBindUrl)}</code></dd>");
            }

            if (!string.IsNullOrWhiteSpace(workspace.DashboardPageUrl))
            {
                html.AppendLine($"<dt>Dashboard URL</dt><dd><a href=\"{Encode(workspace.DashboardPageUrl)}\" target=\"_blank\" rel=\"noreferrer\">{Encode(workspace.DashboardPageUrl)}</a></dd>");
            }

            if (workspace.HostedDashboardPageUrls is { Count: > 0 })
            {
                html.AppendLine($"<dt>Hosted URLs</dt><dd>{RenderHostedDashboardLinks(workspace.HostedDashboardPageUrls)}</dd>");
            }

            if (!string.IsNullOrWhiteSpace(workspace.HostedAccessNote))
            {
                html.AppendLine($"<dt>Hosted access</dt><dd>{Encode(workspace.HostedAccessNote)}</dd>");
            }

            if (!string.IsNullOrWhiteSpace(workspace.SourceSurveyUrl))
            {
                html.AppendLine($"<dt>Source survey</dt><dd><a href=\"{Encode(workspace.SourceSurveyUrl)}\" target=\"_blank\" rel=\"noreferrer\">{Encode(workspace.SourceSurveyUrl)}</a></dd>");
            }

            if (workspace.HostedSourceSurveyUrls is { Count: > 0 })
            {
                html.AppendLine($"<dt>Hosted source survey</dt><dd>{RenderHostedDashboardLinks(workspace.HostedSourceSurveyUrls)}</dd>");
            }

            html.AppendLine($"<dt>Restart command</dt><dd><code>{Encode(workspace.DashboardRestartCommand)}</code></dd>");
            html.AppendLine("</dl>");
            RenderDashboardProcessDiagnostic(html, workspace.ProcessDiagnostic, workspaceLabel);
            RenderBuildTestCleanupPlan(html, workspace);
            RenderBuildTestRunHistory(html, workspace.BuildTestRuns ?? []);
            html.AppendLine("<div class=\"linkbar\"><a href=\"/api/source-survey\" target=\"_blank\" rel=\"noreferrer\">Open source survey</a> <a href=\"/api/system/dashboard-host\" target=\"_blank\" rel=\"noreferrer\">Open hosted URL metadata</a> <a href=\"/api/system/processes\" target=\"_blank\" rel=\"noreferrer\">Open process diagnostic</a> <a href=\"/api/system/build-test-cleanup\" target=\"_blank\" rel=\"noreferrer\">Open build/test cleanup plan</a></div>");
            html.AppendLine("<div class=\"buttonbar\">");
            html.AppendLine("<button class=\"primary\" type=\"button\" data-action-button=\"/api/system/run-build-test-cycle\">Run dashboard build/test cycle</button>");
            html.AppendLine("</div>");
            html.AppendLine("<form class=\"controls compact\" data-action=\"/api/system/stop-dashboard\">");
            html.AppendLine("<button type=\"submit\">Stop dashboard for build/test</button>");
            html.AppendLine("</form>");
            html.AppendLine("</div>");
        }

        RenderContinuationWatches(html, continuationWatches);
        html.AppendLine("<div id=\"op-status\" class=\"statusline\" aria-live=\"polite\"></div>");
        html.AppendLine("</section>");
    }

    private static void RenderReadOnlyHostedContext(StringBuilder html, DashboardWorkspaceContext workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace.DashboardBindUrl) &&
            string.IsNullOrWhiteSpace(workspace.DashboardPageUrl) &&
            string.IsNullOrWhiteSpace(workspace.SourceSurveyUrl) &&
            workspace.HostedDashboardPageUrls is not { Count: > 0 } &&
            workspace.HostedSourceSurveyUrls is not { Count: > 0 })
        {
            return;
        }

        html.AppendLine("<section>");
        html.AppendLine("<h2>Hosted Dashboard</h2>");
        html.AppendLine("<dl class=\"workspace-context\">");
        if (!string.IsNullOrWhiteSpace(workspace.DashboardBindUrl))
        {
            html.AppendLine($"<dt>Bind URL</dt><dd><code>{Encode(workspace.DashboardBindUrl)}</code></dd>");
        }

        if (!string.IsNullOrWhiteSpace(workspace.DashboardPageUrl))
        {
            html.AppendLine($"<dt>Dashboard URL</dt><dd><a href=\"{Encode(workspace.DashboardPageUrl)}\" target=\"_blank\" rel=\"noreferrer\">{Encode(workspace.DashboardPageUrl)}</a></dd>");
        }

        if (workspace.HostedDashboardPageUrls is { Count: > 0 })
        {
            html.AppendLine($"<dt>Hosted URLs</dt><dd>{RenderHostedDashboardLinks(workspace.HostedDashboardPageUrls)}</dd>");
        }

        if (!string.IsNullOrWhiteSpace(workspace.SourceSurveyUrl))
        {
            html.AppendLine($"<dt>Source survey</dt><dd><a href=\"{Encode(workspace.SourceSurveyUrl)}\" target=\"_blank\" rel=\"noreferrer\">{Encode(workspace.SourceSurveyUrl)}</a></dd>");
        }

        if (workspace.HostedSourceSurveyUrls is { Count: > 0 })
        {
            html.AppendLine($"<dt>Hosted source survey</dt><dd>{RenderHostedDashboardLinks(workspace.HostedSourceSurveyUrls)}</dd>");
        }

        if (!string.IsNullOrWhiteSpace(workspace.HostedAccessNote))
        {
            html.AppendLine($"<dt>Hosted access</dt><dd>{Encode(workspace.HostedAccessNote)}</dd>");
        }

        if (!string.IsNullOrWhiteSpace(workspace.DashboardRestartCommand))
        {
            html.AppendLine($"<dt>Restart command</dt><dd><code>{Encode(workspace.DashboardRestartCommand)}</code></dd>");
        }

        html.AppendLine("</dl>");
        html.AppendLine("</section>");
    }

    private static void RenderDashboardProcessDiagnostic(
        StringBuilder html,
        DashboardProcessDiagnostic? diagnostic,
        string workspaceLabel)
    {
        if (diagnostic is null)
        {
            return;
        }

        var hasSiblings = diagnostic.SiblingProcesses.Count > 0;
        html.AppendLine($"<div class=\"{(hasSiblings ? "attention" : "ok")}\">");
        var diagnosticLabel = workspaceLabel switch
        {
            "Hosted dashboard" => "Hosted process diagnostic",
            "Local dashboard" => "Local process diagnostic",
            _ => "Prototype process diagnostic"
        };
        html.AppendLine($"<strong>{Encode(diagnosticLabel)}</strong><br>{Encode(diagnostic.Message)}");
        html.AppendLine($"<div class=\"meta\">Current PID <code>{diagnostic.CurrentProcessId}</code> &middot; process <code>{Encode(diagnostic.ProcessName)}</code> &middot; listening ports {RenderPortList(diagnostic.CurrentListeningPorts)}</div>");
        if (!string.IsNullOrWhiteSpace(diagnostic.CurrentExecutablePath))
        {
            html.AppendLine($"<div class=\"meta\">Executable <code>{Encode(diagnostic.CurrentExecutablePath)}</code></div>");
        }

        if (hasSiblings)
        {
            html.AppendLine("<table><thead><tr><th>Sibling PID</th><th>Ports</th><th>Started</th><th>Executable</th><th>Safe stop</th><th>Detail</th></tr></thead><tbody>");
            foreach (var sibling in diagnostic.SiblingProcesses)
            {
                html.AppendLine("<tr>");
                html.AppendLine($"<td><code>{sibling.ProcessId}</code></td>");
                html.AppendLine($"<td>{RenderPortList(sibling.ListeningPorts)}</td>");
                html.AppendLine($"<td>{Encode(sibling.StartedAt?.ToString("u") ?? "unknown")}</td>");
                html.AppendLine($"<td><code>{Encode(sibling.ExecutablePath ?? "unavailable")}</code></td>");
                html.AppendLine($"<td><code>{Encode(sibling.SafeStopCommand)}</code></td>");
                html.AppendLine($"<td>{Encode(sibling.Detail)}</td>");
                html.AppendLine("</tr>");
            }

            html.AppendLine("</tbody></table>");
            html.AppendLine("<p class=\"section-note\">Use dashboard self-stop for the current PID. For stale siblings, stop only the exact PID after confirming it is not the active dashboard.</p>");
        }

        html.AppendLine("</div>");
    }

    private static string RenderPortList(IReadOnlyList<int> ports)
    {
        return ports.Count == 0
            ? "<span class=\"meta\">unknown</span>"
            : string.Join(", ", ports.Select(port => $"<code>{port}</code>"));
    }

    private static string GetWorkspaceLabel(DashboardWorkspaceContext workspace)
    {
        if (!workspace.RootDirectory.Equals(workspace.ExecutionDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return "Prototype workspace";
        }

        return IsWildcardBindUrl(workspace.DashboardBindUrl)
            ? "Hosted dashboard"
            : "Local dashboard";
    }

    private static bool IsWildcardBindUrl(string? bindUrl)
    {
        if (string.IsNullOrWhiteSpace(bindUrl) ||
            !Uri.TryCreate(bindUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Host.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("*", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("+", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("::", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("[::]", StringComparison.OrdinalIgnoreCase);
    }

    private static string RenderHostedDashboardLinks(IReadOnlyList<string> urls)
    {
        return string.Join("<br>", urls.Select(url => $"<a href=\"{Encode(url)}\" target=\"_blank\" rel=\"noreferrer\">{Encode(url)}</a>"));
    }

    private static void RenderBuildTestCleanupPlan(StringBuilder html, DashboardWorkspaceContext workspace)
    {
        var diagnostic = workspace.ProcessDiagnostic;
        html.AppendLine("<details class=\"workspace-context\">");
        html.AppendLine("<summary><strong>Build/test cleanup plan</strong></summary>");
        html.AppendLine("<p class=\"section-note\">Dashboard-owned cycle: click <strong>Run dashboard build/test cycle</strong> to start the coordinated helper as a background operation with log files under the orchestrator log directory.</p>");
        html.AppendLine($"<p class=\"section-note\">Single-command cycle: <code>.\\scripts\\Invoke-DashboardBuildTestCycle.ps1 -DashboardUrl {Encode(ExtractDashboardUrl(workspace))}</code></p>");
        html.AppendLine("<ol class=\"context-list\">");
        html.AppendLine("<li>Dashboard run endpoint: <code>POST /api/system/run-build-test-cycle</code>.</li>");
        html.AppendLine($"<li>Stop current dashboard PID <code>{workspace.DashboardProcessId}</code> with <code>POST /api/system/stop-dashboard</code>.</li>");
        if (diagnostic is null || diagnostic.SiblingProcesses.Count == 0)
        {
            html.AppendLine("<li>No sibling dashboard processes are currently detected.</li>");
        }
        else
        {
            html.AppendLine("<li>Stop only stale sibling dashboard PIDs after checking their ports:");
            html.AppendLine("<ul class=\"context-list\">");
            foreach (var sibling in diagnostic.SiblingProcesses)
            {
                html.AppendLine($"<li>PID <code>{sibling.ProcessId}</code>, ports {RenderPortList(sibling.ListeningPorts)}, command <code>{Encode(sibling.SafeStopCommand)}</code></li>");
            }

            html.AppendLine("</ul></li>");
        }

        html.AppendLine("<li>Verify no dashboard app process remains: <code>Get-Process Mcg.AgentOrchestrator.App -ErrorAction SilentlyContinue</code>.</li>");
        html.AppendLine("<li>Run <code>dotnet build Mcg.AgentOrchestrator.sln --no-restore</code>.</li>");
        html.AppendLine("<li>Run <code>dotnet test Mcg.AgentOrchestrator.sln --no-build</code>.</li>");
        html.AppendLine($"<li>Restart with <code>{Encode(workspace.DashboardRestartCommand)}</code>.</li>");
        html.AppendLine("</ol>");
        html.AppendLine("</details>");
    }

    private static void RenderBuildTestRunHistory(StringBuilder html, IReadOnlyList<DashboardBuildTestRunSummaryDto> runs)
    {
        html.AppendLine("<details class=\"workspace-context\" open>");
        html.AppendLine("<summary><strong>Build/test run history</strong></summary>");
        html.AppendLine("<div class=\"linkbar\"><a href=\"/api/system/build-test-runs\" target=\"_blank\" rel=\"noreferrer\">Open build/test run JSON</a></div>");
        if (runs.Count == 0)
        {
            html.AppendLine("<p class=\"meta\">No dashboard-owned build/test cycle runs have been recorded yet.</p>");
            html.AppendLine("</details>");
            return;
        }

        html.AppendLine("<table><thead><tr><th>Run</th><th>Status</th><th>Build</th><th>Tests</th><th>Logs</th></tr></thead><tbody>");
        foreach (var run in runs.Take(5))
        {
            var statusClass = run.Status.Equals("Passed", StringComparison.OrdinalIgnoreCase)
                ? "ok"
                : run.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase)
                    ? "bad"
                    : "meta";
            html.AppendLine("<tr>");
            html.AppendLine($"<td><code>{Encode(run.Stamp)}</code><br><span class=\"meta\">{Encode(run.StartedAt?.ToString("u") ?? "unknown")}</span></td>");
            html.AppendLine($"<td class=\"{statusClass}\">{Encode(run.Status)}</td>");
            html.AppendLine($"<td>{Encode(run.BuildSucceeded ? "passed" : "not passed")}</td>");
            html.AppendLine($"<td>{Encode(run.TestSucceeded ? "passed" : "not passed")}</td>");
            html.AppendLine("<td>");
            if (run.HasOutputLog)
            {
                html.AppendLine($"<a href=\"{Encode(DashboardEndpoints.BuildTestLogFileUrl(run.OutputLogPath))}\" target=\"_blank\" rel=\"noreferrer\">output</a>");
            }
            if (run.HasErrorLog)
            {
                html.AppendLine($" <a href=\"{Encode(DashboardEndpoints.BuildTestLogFileUrl(run.ErrorLogPath))}\" target=\"_blank\" rel=\"noreferrer\">error</a>");
            }
            html.AppendLine($"<br><code>{Encode(run.OutputLogPath)}</code>");
            html.AppendLine("</td>");
            html.AppendLine("</tr>");
            if (!string.IsNullOrWhiteSpace(run.OutputPreview) || !string.IsNullOrWhiteSpace(run.ErrorPreview))
            {
                html.AppendLine("<tr class=\"task-summary-row\"><td colspan=\"5\">");
                if (!string.IsNullOrWhiteSpace(run.OutputPreview))
                {
                    html.AppendLine("<strong>Output preview</strong>");
                    html.AppendLine($"<pre>{Encode(run.OutputPreview)}</pre>");
                }
                if (!string.IsNullOrWhiteSpace(run.ErrorPreview))
                {
                    html.AppendLine("<strong>Error preview</strong>");
                    html.AppendLine($"<pre>{Encode(run.ErrorPreview)}</pre>");
                }
                html.AppendLine("</td></tr>");
            }
        }

        html.AppendLine("</tbody></table>");
        html.AppendLine("</details>");
    }

    private static string ExtractDashboardUrl(DashboardWorkspaceContext workspace)
    {
        if (!string.IsNullOrWhiteSpace(workspace.DashboardPageUrl) &&
            Uri.TryCreate(workspace.DashboardPageUrl, UriKind.Absolute, out var pageUri))
        {
            var builder = new UriBuilder(pageUri)
            {
                Path = string.Empty,
                Query = string.Empty,
                Fragment = string.Empty
            };
            return builder.Uri.ToString();
        }

        var parts = workspace.DashboardRestartCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 ? parts[2] : "http://localhost:5087/";
    }

    private static void RenderContinuationWatches(StringBuilder html, IReadOnlyList<DashboardContinuationStatusDto> watches)
    {
        html.AppendLine("<div class=\"workspace-context\">");
        html.AppendLine("<strong>Server continuation</strong>");
        if (watches.Count == 0)
        {
            html.AppendLine("<p class=\"meta\">No server-side continuation watches have run in this dashboard session.</p>");
            html.AppendLine("<div class=\"linkbar\"><a href=\"/api/continuations/summary\" target=\"_blank\" rel=\"noopener\">Open continuation summary</a> <a href=\"/api/continuations\" target=\"_blank\" rel=\"noopener\">Open continuation status</a></div>");
            html.AppendLine("</div>");
            return;
        }

        html.AppendLine("<table><thead><tr><th>Goal</th><th>Status</th><th>Source</th><th>Polls</th><th>Last check</th><th>Next check</th><th>Stop reason</th></tr></thead><tbody>");
        foreach (var watch in watches.Take(6))
        {
            var status = watch.IsRunning ? "Running" : watch.LastError is null ? "Stopped" : "Failed";
            var cls = watch.IsRunning || watch.LastError is null ? "ok" : "bad";
            var source = watch.RestoredFromStore ? "Restored from durable store" : "Current process";
            html.AppendLine("<tr>");
            html.AppendLine($"<td><a href=\"/api/goals/{Encode(watch.GoalId[..8])}\" target=\"_blank\" rel=\"noopener\">{Encode(watch.GoalId[..8])}</a></td>");
            html.AppendLine($"<td class=\"{cls}\">{status}</td>");
            html.AppendLine($"<td>{Encode(source)}</td>");
            html.AppendLine($"<td>{watch.IterationCount}</td>");
            html.AppendLine($"<td>{Encode(watch.LastCheckedAt?.ToString("u") ?? "pending")}</td>");
            html.AppendLine($"<td>{Encode(watch.NextCheckAt?.ToString("u") ?? "poll interval")}</td>");
            html.AppendLine($"<td>{Encode(watch.LastError ?? watch.StopReason)}</td>");
            html.AppendLine("</tr>");
        }

        html.AppendLine("</tbody></table>");
        html.AppendLine("<div class=\"linkbar\"><a href=\"/api/continuations/summary\" target=\"_blank\" rel=\"noopener\">Open continuation summary</a> <a href=\"/api/continuations\" target=\"_blank\" rel=\"noopener\">Open continuation status</a></div>");
        html.AppendLine("</div>");
    }

    private static void RenderHealthReport(StringBuilder html, OrchestratorHealthReport report, bool enableOperatorControls)
    {
        html.AppendLine("<section>");
        html.AppendLine("<h2>Setup Doctor</h2>");
        html.AppendLine($"<p class=\"{(report.IsReady ? "ok" : "bad")}\">Ready: {report.IsReady}</p>");

        html.AppendLine("<h3>Providers</h3>");
        html.AppendLine("<table><thead><tr><th>Provider</th><th>Mode</th><th>Detail</th><th>Smoke</th></tr></thead><tbody>");
        foreach (var provider in report.Providers)
        {
            var smoke = enableOperatorControls
                ? RenderProviderSmokeControl(provider.ProviderName)
                : "<span class=\"meta\">operator only</span>";
            html.AppendLine($"<tr><td>{Encode(provider.ProviderName)}</td><td>{Encode(provider.Mode)}</td><td>{Encode(provider.Detail)}</td><td>{smoke}</td></tr>");
        }
        html.AppendLine("</tbody></table>");

        html.AppendLine("<h3>Agents</h3>");
        html.AppendLine(enableOperatorControls
            ? "<table><thead><tr><th>Role</th><th>Agent</th><th>Execution</th><th>API model</th><th>Subscription launcher</th><th>Status</th><th>Configure</th></tr></thead><tbody>"
            : "<table><thead><tr><th>Role</th><th>Agent</th><th>Execution</th><th>API model</th><th>Subscription launcher</th><th>Status</th></tr></thead><tbody>");
        var defaultAgentProvider = ResolveDefaultAgentProvider(report.Providers);
        foreach (var agent in report.Agents)
        {
            var providerForDefaults = string.IsNullOrWhiteSpace(agent.ProviderName)
                ? defaultAgentProvider
                : agent.ProviderName;
            html.AppendLine("<tr>");
            var subscription = string.IsNullOrWhiteSpace(agent.SubscriptionProfileName)
                ? "<span class=\"meta\">none</span>"
                : $"{Encode(agent.SubscriptionProfileName)}<br><span class=\"meta\">{Encode(agent.SubscriptionModelAlias ?? "default CLI model")}</span>";
            var complexLabel = agent.ComplexModelName is not null
                ? $"<br><span class=\"meta\">complex: {Encode(agent.ComplexModelName)} reasoning {Encode(agent.ComplexReasoningEffort ?? "default")} @ {DisplayMaxTokens(agent.ComplexProviderName ?? providerForDefaults, agent.ComplexMaxOutputTokens, complex: true)}</span>"
                : "";
            html.AppendLine($"<td>{agent.Role}</td><td>{Encode(agent.AgentName)}</td><td>{Encode(Display(agent.ExecutionPolicy))}</td><td>{Encode(agent.ProviderName)}<br><span class=\"meta\">{Encode(agent.ModelName)}</span><br><span class=\"meta\">reasoning: {Encode(agent.ReasoningEffort ?? "default")}</span><br><span class=\"meta\">max tokens: {DisplayMaxTokens(providerForDefaults, agent.MaxOutputTokens, complex: false)}</span>{complexLabel}</td><td>{subscription}</td>");
            html.AppendLine($"<td class=\"{(agent.IsValid ? "ok" : "bad")}\">{Encode(agent.Detail)}</td>");
            if (enableOperatorControls)
            {
                html.AppendLine($"<td>{RenderAgentConfigurationForm(agent, report.WorkerProfiles, defaultAgentProvider)}</td>");
            }

            html.AppendLine("</tr>");
        }
        html.AppendLine("</tbody></table>");

        html.AppendLine("<h3>Worker Profiles</h3>");
        html.AppendLine(enableOperatorControls
            ? "<table><thead><tr><th>Name</th><th>Executable</th><th>Template</th><th>Scope</th><th>Patch</th><th>Status</th><th>Configure</th></tr></thead><tbody>"
            : "<table><thead><tr><th>Name</th><th>Executable</th><th>Template</th><th>Scope</th><th>Patch</th><th>Status</th></tr></thead><tbody>");
        foreach (var profile in report.WorkerProfiles)
        {
            html.AppendLine("<tr>");
            html.AppendLine($"<td>{Encode(profile.Name)}</td><td>{Encode(profile.Executable)}</td>");
            html.AppendLine($"<td><code>{Encode(profile.CommandTemplate)}</code></td>");
            html.AppendLine($"<td>{(profile.IsOptional ? "Optional" : "Required")}</td>");
            html.AppendLine($"<td class=\"{(profile.IsPatchCapable ? "ok" : "bad")}\">{(profile.IsPatchCapable ? "Patch-capable" : "No patching")}</td>");
            var profileOk = profile.IsResolvable &&
                (!profile.IsEchoOnly || profile.Name.Equals("local-echo", StringComparison.OrdinalIgnoreCase));
            html.AppendLine($"<td class=\"{(profileOk ? "ok" : "bad")}\">{Encode(profile.Detail)}</td>");
            if (enableOperatorControls)
            {
                html.AppendLine($"<td>{RenderWorkerProfileForm(profile.Name, profile.CommandTemplate)}</td>");
            }

            html.AppendLine("</tr>");
        }
        html.AppendLine("</tbody></table>");
        if (enableOperatorControls)
        {
            html.AppendLine("<h3>Add Worker Profile</h3>");
            html.AppendLine(RenderWorkerProfileForm(string.Empty, string.Empty));
        }

        html.AppendLine("</section>");
    }

    private static string RenderProviderSmokeControl(string providerName)
    {
        var smokeTarget = providerName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
            ? "openai"
            : providerName.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
                ? "anthropic"
                : Uri.EscapeDataString(providerName.ToLowerInvariant());
        if (providerName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) ||
            providerName.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            return "<form class=\"controls compact\" data-action=\"/api/provider-smoke\">" +
                $"<input type=\"hidden\" name=\"target\" value=\"{Encode(smokeTarget)}\">" +
                "<input type=\"hidden\" name=\"confirmPaidSmoke\" value=\"true\">" +
                "<button type=\"submit\">Smoke</button></form>";
        }

        return $"<a href=\"/api/provider-smoke?target={Encode(smokeTarget)}\" target=\"_blank\" rel=\"noopener\">Smoke</a>";
    }

    private static string RenderAgentConfigurationForm(
        AgentConfigurationValidation agent,
        IReadOnlyList<WorkerProfileValidation> workerProfiles,
        string defaultProvider)
    {
        var role = agent.Role.ToString();
        var provider = string.IsNullOrWhiteSpace(agent.ProviderName) ? defaultProvider : agent.ProviderName;
        var modelName = string.IsNullOrWhiteSpace(agent.ModelName) ? DefaultApiModel(provider) : agent.ModelName;
        var name = string.IsNullOrWhiteSpace(agent.AgentName) ? $"{provider} {role.ToLowerInvariant()}" : agent.AgentName;
        var subscriptionProfile = string.IsNullOrWhiteSpace(agent.SubscriptionProfileName)
            ? DefaultSubscriptionProfile(provider)
            : agent.SubscriptionProfileName;
        var complexProvider = agent.ComplexProviderName ?? provider;
        var html = new StringBuilder();
        html.AppendLine($"<form class=\"controls compact\" data-action=\"/api/agents\" data-agent-config=\"true\" data-default-provider=\"{Encode(defaultProvider)}\">");
        html.AppendLine($"<input type=\"hidden\" name=\"role\" value=\"{Encode(role)}\">");
        html.AppendLine($"<input type=\"hidden\" name=\"name\" value=\"{Encode(name)}\" data-agent-name>");
        html.AppendLine("<div class=\"field\"><label>Execution</label><select name=\"executionPolicy\">");
        html.AppendLine(RenderExecutionPolicyOption(AgentExecutionPolicy.ApiOnly, agent.ExecutionPolicy));
        html.AppendLine(RenderExecutionPolicyOption(AgentExecutionPolicy.PreferSubscription, agent.ExecutionPolicy));
        html.AppendLine(RenderExecutionPolicyOption(AgentExecutionPolicy.SubscriptionOnly, agent.ExecutionPolicy));
        html.AppendLine(RenderExecutionPolicyOption(AgentExecutionPolicy.AnyAvailable, agent.ExecutionPolicy));
        html.AppendLine("</select></div>");
        html.AppendLine("<div class=\"field\"><label>Provider</label><select name=\"providerName\">");
        html.AppendLine(RenderProviderOption("OpenAI", provider));
        html.AppendLine(RenderProviderOption("Anthropic", provider));
        html.AppendLine(RenderProviderOption("Ollama", provider));
        html.AppendLine("</select></div>");
        html.AppendLine("<div class=\"field\"><label>API model</label><select name=\"modelName\" data-provider-options=\"apiModels\" required>");
        RenderProviderSelectOptions(html, provider, modelName, ApiModelOptions);
        html.AppendLine("</select></div>");
        html.AppendLine("<div class=\"field\"><label>API reasoning</label><select name=\"reasoningEffort\" data-provider-options=\"apiReasoning\">");
        RenderProviderSelectOptions(html, provider, agent.ReasoningEffort, ApiReasoningOptions);
        html.AppendLine("</select></div>");
        html.AppendLine($"<div class=\"field\"><label>Max tokens</label><input type=\"number\" name=\"maxOutputTokens\" min=\"1\" placeholder=\"{DefaultMaxTokensPlaceholder(provider)}\" value=\"{(agent.MaxOutputTokens.HasValue ? agent.MaxOutputTokens.Value.ToString() : "")}\" style=\"width:5em\"></div>");
        html.AppendLine("<div class=\"field\"><label>Subscription profile</label><select name=\"subscriptionProfileName\" data-provider-options=\"subscriptionProfiles\">");
        RenderSubscriptionProfileOptions(html, provider, subscriptionProfile, workerProfiles);
        html.AppendLine("</select></div>");
        html.AppendLine("<div class=\"field\"><label>CLI model alias</label><select name=\"subscriptionModelAlias\" data-provider-options=\"subscriptionModels\">");
        RenderProviderSelectOptions(html, provider, agent.SubscriptionModelAlias, SubscriptionModelOptions);
        html.AppendLine("</select></div>");
        html.AppendLine("<div class=\"field\"><label>CLI reasoning</label><select name=\"subscriptionReasoningEffort\" data-provider-options=\"subscriptionReasoning\">");
        RenderProviderSelectOptions(html, provider, agent.SubscriptionReasoningEffort, SubscriptionReasoningOptions);
        html.AppendLine("</select></div>");
        html.AppendLine("<details style=\"margin-top:0.5em\"><summary style=\"cursor:pointer;font-size:0.85em\">Complex task model (auto-selected for ambitious tasks)</summary>");
        html.AppendLine($"<div class=\"field\"><label>Complex provider</label><input type=\"text\" name=\"complexProviderName\" value=\"{Encode(agent.ComplexProviderName ?? "")}\" placeholder=\"same as above\"></div>");
        html.AppendLine($"<div class=\"field\"><label>Complex model</label><input type=\"text\" name=\"complexModelName\" value=\"{Encode(agent.ComplexModelName ?? "")}\" placeholder=\"none\"></div>");
        html.AppendLine("<div class=\"field\"><label>Complex reasoning</label><select name=\"complexReasoningEffort\" data-provider-options=\"apiReasoning\">");
        RenderProviderSelectOptions(html, complexProvider, agent.ComplexReasoningEffort, ApiReasoningOptions);
        html.AppendLine("</select></div>");
        html.AppendLine($"<div class=\"field\"><label>Complex max tokens</label><input type=\"number\" name=\"complexMaxOutputTokens\" min=\"1\" placeholder=\"{DefaultComplexMaxTokensPlaceholder(complexProvider)}\" value=\"{(agent.ComplexMaxOutputTokens.HasValue ? agent.ComplexMaxOutputTokens.Value.ToString() : "")}\" style=\"width:5em\"></div>");
        html.AppendLine("</details>");
        html.AppendLine("<button type=\"submit\">Save</button>");
        html.AppendLine("</form>");
        return html.ToString();
    }

    private static string ResolveDefaultAgentProvider(IReadOnlyList<ProviderConfigurationStatus> providers)
    {
        return providers.Any(provider =>
            provider.ProviderName.Equals("Ollama", StringComparison.OrdinalIgnoreCase) &&
            provider.IsConfigured)
            ? "Ollama"
            : "OpenAI";
    }

    private static string RenderExecutionPolicyOption(AgentExecutionPolicy option, AgentExecutionPolicy selected)
    {
        var selectedAttribute = option == selected ? " selected" : string.Empty;
        return $"<option value=\"{option}\"{selectedAttribute}>{Encode(Display(option))}</option>";
    }

    private static string RenderProviderOption(string option, string selected)
    {
        var selectedAttribute = option.Equals(selected, StringComparison.OrdinalIgnoreCase) ? " selected" : string.Empty;
        return $"<option value=\"{Encode(option)}\"{selectedAttribute}>{Encode(option)}</option>";
    }

    private static string DefaultSubscriptionProfile(string provider)
    {
        if (provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            return "codex-cli";
        }

        if (provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            return "claude-cli";
        }

        return string.Empty;
    }

    private static IReadOnlyList<(string Value, string Label)> ApiModelOptions(string provider)
    {
        if (provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                ("claude-sonnet-4-20250514", "Claude Sonnet 4"),
                ("claude-opus-4-20250514", "Claude Opus 4")
            ];
        }

        if (provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                ("qwen2.5-coder:7b", "Qwen2.5 Coder 7B"),
                ("qwen3:8b", "Qwen3 8B")
            ];
        }

        return
            [
                ("gpt-5.4-mini", "GPT-5.4 mini"),
                ("gpt-5.4", "GPT-5.4"),
                ("gpt-5.4-pro", "GPT-5.4 pro"),
                ("gpt-5.5", "GPT-5.5"),
                ("gpt-5.5-pro", "GPT-5.5 pro"),
                ("gpt-5.4-nano", "GPT-5.4 nano"),
                ("gpt-5.3-codex", "GPT-5.3-Codex"),
                ("gpt-5-mini", "GPT-5 mini"),
                ("gpt-5-nano", "GPT-5 nano"),
                ("gpt-5.2", "GPT-5.2 (previous)")
            ];
    }

    private static string DefaultApiModel(string provider) => ApiModelOptions(provider)[0].Value;

    private static string DisplayMaxTokens(string provider, int? configured, bool complex)
    {
        return configured.HasValue
            ? configured.Value.ToString()
            : (complex ? DefaultComplexMaxTokensPlaceholder(provider) : DefaultMaxTokensPlaceholder(provider)).ToString();
    }

    private static int DefaultMaxTokensPlaceholder(string provider)
    {
        return provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) ||
            provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
                ? AgentCatalog.RoutineApiMaxOutputTokens
                : 8192;
    }

    private static int DefaultComplexMaxTokensPlaceholder(string provider)
    {
        return provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) ||
            provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
                ? AgentCatalog.ComplexApiMaxOutputTokens
                : 8192;
    }

    private static IReadOnlyList<(string Value, string Label)> ApiReasoningOptions(string provider)
    {
        return provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase) ||
            provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase)
            ? [("", "Default")]
            : [("", "Default"), ("none", "None"), ("low", "Low"), ("medium", "Medium"), ("high", "High"), ("xhigh", "Extra high")];
    }

    private static IReadOnlyList<(string Value, string Label)> SubscriptionModelOptions(string provider)
    {
        if (provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
        {
            return [("", "No subscription model")];
        }

        return provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
            ? [("", "Default CLI model"), ("claude-sonnet", "Claude Sonnet"), ("claude-opus", "Claude Opus")]
            : [("", "Default CLI model"), ("gpt-5.3-codex", "GPT-5.3-Codex"), ("gpt-5.5", "GPT-5.5")];
    }

    private static IReadOnlyList<(string Value, string Label)> SubscriptionReasoningOptions(string provider)
    {
        return provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase) ||
            provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase)
            ? [("", "Default")]
            : [("", "Default"), ("none", "None"), ("low", "Low"), ("medium", "Medium"), ("high", "High"), ("xhigh", "Extra high")];
    }

    private static void RenderProviderSelectOptions(
        StringBuilder html,
        string provider,
        string? selected,
        Func<string, IReadOnlyList<(string Value, string Label)>> optionsFactory)
    {
        var options = optionsFactory(provider);
        foreach (var option in options)
        {
            html.AppendLine(RenderSelectOption(option.Value, option.Label, selected));
        }

        RenderUnknownSelectedOption(html, selected, options);
    }

    private static void RenderSubscriptionProfileOptions(
        StringBuilder html,
        string provider,
        string selected,
        IReadOnlyList<WorkerProfileValidation> workerProfiles)
    {
        var preferred = DefaultSubscriptionProfile(provider);
        var names = workerProfiles
            .Select(profile => profile.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name.Equals(preferred, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!names.Contains(preferred, StringComparer.OrdinalIgnoreCase))
        {
            names.Insert(0, preferred);
        }

        if (!names.Contains(string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            names.Insert(0, string.Empty);
        }

        foreach (var name in names)
        {
            html.AppendLine(RenderSelectOption(name, string.IsNullOrWhiteSpace(name) ? "None" : name, selected));
        }

        RenderUnknownSelectedOption(html, selected, names.Select(name => (Value: name, Label: string.IsNullOrWhiteSpace(name) ? "None" : name)).ToList());
    }

    private static string RenderSelectOption(string value, string label, string? selected)
    {
        var selectedAttribute = string.Equals(value, selected ?? string.Empty, StringComparison.OrdinalIgnoreCase) ? " selected" : string.Empty;
        return $"<option value=\"{Encode(value)}\"{selectedAttribute}>{Encode(label)}</option>";
    }

    private static void RenderUnknownSelectedOption(
        StringBuilder html,
        string? selected,
        IReadOnlyList<(string Value, string Label)> knownOptions)
    {
        if (string.IsNullOrWhiteSpace(selected) ||
            knownOptions.Any(option => option.Value.Equals(selected, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        html.AppendLine(RenderSelectOption(selected, $"Configured: {selected}", selected));
    }

    private static string RenderWorkerProfileForm(string name, string commandTemplate)
    {
        var html = new StringBuilder();
        html.AppendLine("<form class=\"controls compact\" data-action=\"/api/worker-profiles\">");
        html.AppendLine($"<div class=\"field\"><label>Name</label><input name=\"name\" value=\"{Encode(name)}\" required></div>");
        html.AppendLine($"<div class=\"field\"><label>Command template</label><input class=\"wide\" name=\"commandTemplate\" value=\"{Encode(commandTemplate)}\" required></div>");
        html.AppendLine("<button type=\"submit\">Save</button>");
        html.AppendLine("</form>");
        return html.ToString();
    }
}
