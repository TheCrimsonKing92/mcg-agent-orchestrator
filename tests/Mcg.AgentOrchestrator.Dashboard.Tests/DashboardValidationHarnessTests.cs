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

    Assert.Contains("ScriptPath must be under", runner, StringComparison.Ordinal);
    Assert.Contains("Invoke-DashboardBrowser.ps1", runner, StringComparison.Ordinal);
    Assert.Contains("--remote-debugging-port=$Port", invoker, StringComparison.Ordinal);
    Assert.Contains(".scratch\\edge-profile", invoker, StringComparison.Ordinal);
    Assert.Contains("CdpTimeoutSeconds", invoker, StringComparison.Ordinal);
    Assert.Contains("/json/new?", invoker, StringComparison.Ordinal);
    Assert.Contains("/json/close/", invoker, StringComparison.Ordinal);
    Assert.Contains("Timed out waiting for Chrome DevTools response", invoker, StringComparison.Ordinal);
    Assert.Contains("Chrome DevTools $Method failed", invoker, StringComparison.Ordinal);
    Assert.Contains("Invoke-RuntimeEvaluate", invoker, StringComparison.Ordinal);
    Assert.Contains("Execution context was destroyed", invoker, StringComparison.Ordinal);
    Assert.Contains("window.__dashboardReady", invoker, StringComparison.Ordinal);
    Assert.Contains("#dashboard-content", invoker, StringComparison.Ordinal);
    Assert.Contains("[void]$socket.ConnectAsync", invoker, StringComparison.Ordinal);
    Assert.Contains("exceptionDetails", invoker, StringComparison.Ordinal);
    Assert.Contains("PSObject.Properties.Name -contains \"exceptionDetails\"", invoker, StringComparison.Ordinal);
    Assert.Contains("PSObject.Properties.Name -contains \"id\"", invoker, StringComparison.Ordinal);
    Assert.Contains("CloseOutputAsync", invoker, StringComparison.Ordinal);
    Assert.Contains("$socket.Abort()", invoker, StringComparison.Ordinal);
    Assert.Contains("Open bounded source survey", smoke, StringComparison.Ordinal);
    Assert.Contains("/api/source-survey?max=8", smoke, StringComparison.Ordinal);
    Assert.Contains("Goal JSON", smoke, StringComparison.Ordinal);
    Assert.Contains("Stop dashboard for build/test", smoke, StringComparison.Ordinal);
    Assert.Contains("Invoke-WebRequest @invokeArgs", api, StringComparison.Ordinal);
    Assert.Contains("ConvertFrom-Json -InputObject", api, StringComparison.Ordinal);
    Assert.Contains("ConvertTo-Json -InputObject", api, StringComparison.Ordinal);
    Assert.Contains("[string[]]$Select", api, StringComparison.Ordinal);
    Assert.Contains("UseBasicParsing", api, StringComparison.Ordinal);
    Assert.Contains("api/system/build-test-cleanup", buildTestCycle, StringComparison.Ordinal);
    Assert.Contains("Stop-Process -Id $processId", buildTestCycle, StringComparison.Ordinal);
    Assert.Contains("dotnet build $Solution --no-restore --verbosity minimal @isolatedArguments", buildTestCycle, StringComparison.Ordinal);
    Assert.Contains("dotnet test --solution $Solution --no-build --verbosity minimal @testIsolationArguments", buildTestCycle, StringComparison.Ordinal);
    Assert.DoesNotContain("--artifacts-path", buildTestCycle, StringComparison.Ordinal);
    Assert.Contains("-maxcpucount:$maxCpuCount", buildTestCycle, StringComparison.Ordinal);
    Assert.Contains("--property:McgIsolatedArtifactsPath=$($buildIsolation.ArtifactsPath)", buildTestCycle, StringComparison.Ordinal);
    Assert.Contains("if ($cycleSucceeded)", buildTestCycle, StringComparison.Ordinal);
    Assert.Contains("Remove-Item -LiteralPath $buildIsolation.RunRoot", buildTestCycle, StringComparison.Ordinal);
    Assert.Contains("Wait-DashboardHealth", buildTestCycle, StringComparison.Ordinal);
    Assert.Contains("ValidateSet(\"create-goal\", \"complete-task\", \"smoke\")", dogfoodAction, StringComparison.Ordinal);
    Assert.Contains("form[data-action=\"/api/goals\"]", dogfoodAction, StringComparison.Ordinal);
    Assert.Contains("window.__dashboardSubmitForm(form)", dogfoodAction, StringComparison.Ordinal);
    Assert.Contains("New-DashboardGoalUrl", dogfoodAction, StringComparison.Ordinal);
    Assert.Contains("/goal/", dogfoodAction, StringComparison.Ordinal);
    Assert.Contains("complete-verify", dogfoodAction, StringComparison.Ordinal);
    Assert.Contains("complete-and-verify API fallback", dogfoodAction, StringComparison.Ordinal);
    Assert.Contains("JSON.stringify({ passed: true, note })", dogfoodAction, StringComparison.Ordinal);
    Assert.Contains("dashboard-smoke.js", dogfoodAction, StringComparison.Ordinal);
    Assert.Contains("Run-DashboardBrowserScript.ps1 .\\scripts\\dashboard-smoke.js", readme, StringComparison.Ordinal);
    Assert.Contains("Invoke-DashboardDogfoodAction.ps1", readme, StringComparison.Ordinal);
    Assert.Contains("--confirm-large-paid-api-prompt", readme, StringComparison.Ordinal);
    Assert.Contains("confirmLargePaidApiPrompt=true", readme, StringComparison.Ordinal);
    Assert.Contains("confirmLargePaidSubscriptionStart=true", readme, StringComparison.Ordinal);
    Assert.Contains("does not fall back to paid providers", readme, StringComparison.Ordinal);
    Assert.Contains("/api/goals/{goalPrefix}/work-summary", agents, StringComparison.Ordinal);
    Assert.Contains("Invoke-DashboardApi.ps1 -Path api/goals/<prefix>/work-summary", agents, StringComparison.Ordinal);
    Assert.Contains("Invoke-DashboardApi.ps1", agents, StringComparison.Ordinal);
    Assert.Contains("Run-DashboardBrowserScript.ps1", agents, StringComparison.Ordinal);
    Assert.Contains("Invoke-DashboardDogfoodAction.ps1", agents, StringComparison.Ordinal);
}
}
