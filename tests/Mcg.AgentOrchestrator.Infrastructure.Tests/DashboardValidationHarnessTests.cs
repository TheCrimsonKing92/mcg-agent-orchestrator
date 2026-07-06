public sealed class DashboardValidationHarnessTests
{
    [Xunit.Fact(DisplayName = "Dashboard_validation_harness_is_checked_in_and_scoped_to_repository_scripts")]
    public void DashboardValidationHarnessIsCheckedInAndScopedToRepositoryScripts()
{
    var root = FindRepositoryRoot();
    var runner = File.ReadAllText(Path.Combine(root, "scripts", "Run-DashboardBrowserScript.ps1"));
    var invoker = File.ReadAllText(Path.Combine(root, "scripts", "Invoke-DashboardBrowser.ps1"));
    var smoke = File.ReadAllText(Path.Combine(root, "scripts", "dashboard-smoke.js"));
    var api = File.ReadAllText(Path.Combine(root, "scripts", "Invoke-DashboardApi.ps1"));
    var buildTestCycle = File.ReadAllText(Path.Combine(root, "scripts", "Invoke-DashboardBuildTestCycle.ps1"));
    var dogfoodAction = File.ReadAllText(Path.Combine(root, "scripts", "Invoke-DashboardDogfoodAction.ps1"));
    var readme = File.ReadAllText(Path.Combine(root, "README.md"));
    var agents = File.ReadAllText(Path.Combine(root, "AGENTS.md"));

    AssertEx.Contains(runner, text => text.Contains("ScriptPath must be under", StringComparison.Ordinal));
    AssertEx.Contains(runner, text => text.Contains("Invoke-DashboardBrowser.ps1", StringComparison.Ordinal));
    AssertEx.Contains(invoker, text => text.Contains("--remote-debugging-port=$Port", StringComparison.Ordinal));
    AssertEx.Contains(invoker, text => text.Contains(".scratch\\edge-profile", StringComparison.Ordinal));
    AssertEx.Contains(invoker, text => text.Contains("CdpTimeoutSeconds", StringComparison.Ordinal));
    AssertEx.Contains(invoker, text => text.Contains("/json/new?", StringComparison.Ordinal));
    AssertEx.Contains(invoker, text => text.Contains("/json/close/", StringComparison.Ordinal));
    AssertEx.Contains(invoker, text => text.Contains("Timed out waiting for Chrome DevTools response", StringComparison.Ordinal));
    AssertEx.Contains(invoker, text => text.Contains("Chrome DevTools $Method failed", StringComparison.Ordinal));
    AssertEx.Contains(invoker, text => text.Contains("Invoke-RuntimeEvaluate", StringComparison.Ordinal));
    AssertEx.Contains(invoker, text => text.Contains("Execution context was destroyed", StringComparison.Ordinal));
    AssertEx.Contains(invoker, text => text.Contains("window.__dashboardReady", StringComparison.Ordinal));
    AssertEx.Contains(invoker, text => text.Contains("#dashboard-content", StringComparison.Ordinal));
    AssertEx.Contains(invoker, text => text.Contains("[void]$socket.ConnectAsync", StringComparison.Ordinal));
    AssertEx.Contains(invoker, text => text.Contains("exceptionDetails", StringComparison.Ordinal));
    AssertEx.Contains(invoker, text => text.Contains("PSObject.Properties.Name -contains \"exceptionDetails\"", StringComparison.Ordinal));
    AssertEx.Contains(invoker, text => text.Contains("PSObject.Properties.Name -contains \"id\"", StringComparison.Ordinal));
    AssertEx.Contains(invoker, text => text.Contains("CloseOutputAsync", StringComparison.Ordinal));
    AssertEx.Contains(invoker, text => text.Contains("$socket.Abort()", StringComparison.Ordinal));
    AssertEx.Contains(smoke, text => text.Contains("Open bounded source survey", StringComparison.Ordinal));
    AssertEx.Contains(smoke, text => text.Contains("/api/source-survey?max=8", StringComparison.Ordinal));
    AssertEx.Contains(smoke, text => text.Contains("Goal JSON", StringComparison.Ordinal));
    AssertEx.Contains(smoke, text => text.Contains("Stop dashboard for build/test", StringComparison.Ordinal));
    AssertEx.Contains(api, text => text.Contains("Invoke-WebRequest @invokeArgs", StringComparison.Ordinal));
    AssertEx.Contains(api, text => text.Contains("ConvertFrom-Json -InputObject", StringComparison.Ordinal));
    AssertEx.Contains(api, text => text.Contains("ConvertTo-Json -InputObject", StringComparison.Ordinal));
    AssertEx.Contains(api, text => text.Contains("[string[]]$Select", StringComparison.Ordinal));
    AssertEx.Contains(api, text => text.Contains("UseBasicParsing", StringComparison.Ordinal));
    AssertEx.Contains(buildTestCycle, text => text.Contains("api/system/build-test-cleanup", StringComparison.Ordinal));
    AssertEx.Contains(buildTestCycle, text => text.Contains("Stop-Process -Id $processId", StringComparison.Ordinal));
    AssertEx.Contains(buildTestCycle, text => text.Contains("dotnet build $Solution --no-restore --verbosity minimal @isolatedArguments", StringComparison.Ordinal));
    AssertEx.Contains(buildTestCycle, text => text.Contains("dotnet test $Solution --no-build --verbosity minimal @isolatedArguments", StringComparison.Ordinal));
    AssertEx.Contains(buildTestCycle, text => text.Contains("--artifacts-path", StringComparison.Ordinal));
    AssertEx.Contains(buildTestCycle, text => text.Contains("-maxcpucount:$maxCpuCount", StringComparison.Ordinal));
    AssertEx.Contains(buildTestCycle, text => text.Contains("Wait-DashboardHealth", StringComparison.Ordinal));
    AssertEx.Contains(dogfoodAction, text => text.Contains("ValidateSet(\"create-goal\", \"complete-task\", \"smoke\")", StringComparison.Ordinal));
    AssertEx.Contains(dogfoodAction, text => text.Contains("form[data-action=\"/api/goals\"]", StringComparison.Ordinal));
    AssertEx.Contains(dogfoodAction, text => text.Contains("window.__dashboardSubmitForm(form)", StringComparison.Ordinal));
    AssertEx.Contains(dogfoodAction, text => text.Contains("New-DashboardGoalUrl", StringComparison.Ordinal));
    AssertEx.Contains(dogfoodAction, text => text.Contains("/goal/", StringComparison.Ordinal));
    AssertEx.Contains(dogfoodAction, text => text.Contains("complete-verify", StringComparison.Ordinal));
    AssertEx.Contains(dogfoodAction, text => text.Contains("complete-and-verify API fallback", StringComparison.Ordinal));
    AssertEx.Contains(dogfoodAction, text => text.Contains("JSON.stringify({ passed: true, note })", StringComparison.Ordinal));
    AssertEx.Contains(dogfoodAction, text => text.Contains("dashboard-smoke.js", StringComparison.Ordinal));
    AssertEx.Contains(readme, text => text.Contains("Run-DashboardBrowserScript.ps1 .\\scripts\\dashboard-smoke.js", StringComparison.Ordinal));
    AssertEx.Contains(readme, text => text.Contains("Invoke-DashboardDogfoodAction.ps1", StringComparison.Ordinal));
    AssertEx.Contains(readme, text => text.Contains("--confirm-large-paid-api-prompt", StringComparison.Ordinal));
    AssertEx.Contains(readme, text => text.Contains("confirmLargePaidApiPrompt=true", StringComparison.Ordinal));
    AssertEx.Contains(readme, text => text.Contains("confirmLargePaidSubscriptionStart=true", StringComparison.Ordinal));
    AssertEx.Contains(readme, text => text.Contains("does not fall back to paid providers", StringComparison.Ordinal));
    AssertEx.Contains(agents, text => text.Contains("/api/goals/{goalPrefix}/work-summary", StringComparison.Ordinal));
    AssertEx.Contains(agents, text => text.Contains("Invoke-DashboardApi.ps1 -Path api/goals/<prefix>/work-summary", StringComparison.Ordinal));
    AssertEx.Contains(agents, text => text.Contains("Invoke-DashboardApi.ps1", StringComparison.Ordinal));
    AssertEx.Contains(agents, text => text.Contains("Run-DashboardBrowserScript.ps1", StringComparison.Ordinal));
    AssertEx.Contains(agents, text => text.Contains("Invoke-DashboardDogfoodAction.ps1", StringComparison.Ordinal));
}
}
