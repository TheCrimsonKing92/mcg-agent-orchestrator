using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Core.Tests;

public sealed class VerificationPolicyCompilerTests
{
    [Xunit.Fact(DisplayName = "Compile_omits_browser_smoke_when_text_signals_but_no_front_end_files")]
    public void CompileOmitsBrowserSmokeWhenTextSignalsButNoFrontEndFiles()
    {
        var policy = VerificationPolicyCompiler.Compile(
            AgentRole.Developer,
            "Fix e2e flakiness in the browser flow",
            "Investigate playwright screenshot failures end-to-end",
            "Run the ui flow smoke checks",
            ["src/Mcg.AgentOrchestrator.Core/Application/SomeService.cs"]);

        Assert.True(!policy.Checks.Any(c => c.Kind == "browser-smoke"),
            "browser-smoke must not fire when no front-end asset is changed");
    }

    [Xunit.Fact(DisplayName = "Compile_adds_browser_smoke_when_text_signals_and_tsx_file_changed")]
    public void CompileAddsBrowserSmokeWhenTextSignalsAndTsxFileChanged()
    {
        var policy = VerificationPolicyCompiler.Compile(
            AgentRole.Developer,
            "Add e2e test for dashboard ui",
            "Update playwright screenshot assertions",
            null,
            ["src/Mcg.AgentOrchestrator.Dashboard/Components/GoalList.tsx"]);

        Assert.True(policy.Checks.Any(c => c.Kind == "browser-smoke"),
            "browser-smoke must fire when text signals and a .tsx file is changed");
    }

    [Xunit.Fact(DisplayName = "Compile_adds_browser_smoke_for_css_and_html_front_end_extensions")]
    public void CompileAddsBrowserSmokeForCssAndHtmlFrontEndExtensions()
    {
        var policyWithCss = VerificationPolicyCompiler.Compile(
            AgentRole.Developer,
            "Update dashboard ui styling for browser",
            string.Empty,
            null,
            ["src/Mcg.AgentOrchestrator.Dashboard/styles/main.css"]);

        var policyWithHtml = VerificationPolicyCompiler.Compile(
            AgentRole.Developer,
            "Fix screenshot layout in browser",
            string.Empty,
            null,
            ["src/Mcg.AgentOrchestrator.Dashboard/index.html"]);

        Assert.True(policyWithCss.Checks.Any(c => c.Kind == "browser-smoke"),
            "browser-smoke must fire for .css front-end asset");
        Assert.True(policyWithHtml.Checks.Any(c => c.Kind == "browser-smoke"),
            "browser-smoke must fire for .html front-end asset");
    }

    [Xunit.Fact(DisplayName = "Compile_adds_browser_smoke_when_dashboard_script_itself_is_changed")]
    public void CompileAddsBrowserSmokeWhenDashboardScriptItselfIsChanged()
    {
        var policy = VerificationPolicyCompiler.Compile(
            AgentRole.Developer,
            "Update the dashboard ui browser smoke script",
            string.Empty,
            null,
            ["scripts/Run-DashboardBrowserScript.ps1"]);

        Assert.True(policy.Checks.Any(c => c.Kind == "browser-smoke"),
            "browser-smoke must fire when the smoke script itself is changed");
    }

    [Xunit.Fact(DisplayName = "Compile_omits_browser_smoke_when_front_end_files_changed_but_no_text_signal")]
    public void CompileOmitsBrowserSmokeWhenFrontEndFilesChangedButNoTextSignal()
    {
        var policy = VerificationPolicyCompiler.Compile(
            AgentRole.Developer,
            "Fix typo in component label",
            "Correct the spelling of 'Recieve' to 'Receive' in GoalList",
            null,
            ["src/Mcg.AgentOrchestrator.Dashboard/Components/GoalList.tsx"]);

        Assert.True(!policy.Checks.Any(c => c.Kind == "browser-smoke"),
            "browser-smoke must not fire when no browser-signal keyword appears in task text");
    }

    [Xunit.Fact(DisplayName = "Compile_adds_browser_smoke_for_windows_backslash_paths")]
    public void CompileAddsBrowserSmokeForWindowsBackslashPaths()
    {
        var policy = VerificationPolicyCompiler.Compile(
            AgentRole.Developer,
            "Fix e2e screenshot in dashboard ui",
            string.Empty,
            null,
            ["src\\Mcg.AgentOrchestrator.Dashboard\\Components\\GoalList.tsx"]);

        Assert.True(policy.Checks.Any(c => c.Kind == "browser-smoke"),
            "browser-smoke must fire for backslash-style Windows paths");
    }
}
