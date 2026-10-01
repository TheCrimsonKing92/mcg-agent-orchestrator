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

    [Xunit.Fact(DisplayName = "Compile_omits_browser_smoke_when_text_signals_and_tsx_file_changed")]
    public void CompileOmitsBrowserSmokeWhenTextSignalsAndTsxFileChanged()
    {
        var policy = VerificationPolicyCompiler.Compile(
            AgentRole.Developer,
            "Add e2e test for dashboard ui",
            "Update playwright screenshot assertions",
            null,
            ["src/Mcg.AgentOrchestrator.Dashboard/Components/GoalList.tsx"]);

        Assert.DoesNotContain(policy.Checks, c => c.Kind == "browser-smoke");
    }

    [Xunit.Fact(DisplayName = "Compile_omits_browser_smoke_for_css_and_html_front_end_extensions")]
    public void CompileOmitsBrowserSmokeForCssAndHtmlFrontEndExtensions()
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

        Assert.DoesNotContain(policyWithCss.Checks, c => c.Kind == "browser-smoke");
        Assert.DoesNotContain(policyWithHtml.Checks, c => c.Kind == "browser-smoke");
    }

    [Xunit.Fact(DisplayName = "Compile_omits_browser_smoke_when_dashboard_script_itself_is_changed")]
    public void CompileOmitsBrowserSmokeWhenDashboardScriptItselfIsChanged()
    {
        var policy = VerificationPolicyCompiler.Compile(
            AgentRole.Developer,
            "Update the dashboard ui browser smoke script",
            string.Empty,
            null,
            ["scripts/Run-DashboardBrowserScript.ps1"]);

        Assert.DoesNotContain(policy.Checks, c => c.Kind == "browser-smoke");
    }

    [Xunit.Fact(DisplayName = "Compile_omits_browser_smoke_when_front_end_files_changed_without_keywords")]
    public void CompileOmitsBrowserSmokeWhenFrontEndFilesChangedWithoutKeywords()
    {
        var policy = VerificationPolicyCompiler.Compile(
            AgentRole.Developer,
            "Fix typo in component label",
            "Correct the spelling of 'Recieve' to 'Receive' in GoalList",
            null,
            ["src/Mcg.AgentOrchestrator.Dashboard/Components/GoalList.tsx"]);

        Assert.DoesNotContain(policy.Checks, c => c.Kind == "browser-smoke");
    }

    [Xunit.Fact(DisplayName = "Compile_omits_browser_smoke_for_wwwroot_front_end_files")]
    public void CompileOmitsBrowserSmokeForWwwrootFrontEndFiles()
    {
        var policy = VerificationPolicyCompiler.Compile(
            AgentRole.Developer,
            "Internal refactor",
            "No front-end terms here.",
            null,
            ["wwwroot/css/app.css"]);

        Assert.DoesNotContain(policy.Checks, c => c.Kind == "browser-smoke");
    }

    [Xunit.Fact(DisplayName = "Compile_omits_browser_smoke_for_client_side_js_assets")]
    public void CompileOmitsBrowserSmokeForClientSideJsAssets()
    {
        var policy = VerificationPolicyCompiler.Compile(
            AgentRole.Developer,
            "Internal refactor",
            "No front-end terms here.",
            null,
            ["src/Mcg.AgentOrchestrator.Dashboard/ClientApp/app.js"]);

        Assert.DoesNotContain(policy.Checks, c => c.Kind == "browser-smoke");
    }

    [Xunit.Fact(DisplayName = "Compile_omits_browser_smoke_for_windows_backslash_paths")]
    public void CompileOmitsBrowserSmokeForWindowsBackslashPaths()
    {
        var policy = VerificationPolicyCompiler.Compile(
            AgentRole.Developer,
            "Fix e2e screenshot in dashboard ui",
            string.Empty,
            null,
            ["src\\Mcg.AgentOrchestrator.Dashboard\\Components\\GoalList.tsx"]);

        Assert.DoesNotContain(policy.Checks, c => c.Kind == "browser-smoke");
    }

    [Xunit.Theory]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Dashboard/Program.cs")]
    [Xunit.InlineData("wwwroot/app.js")]
    [Xunit.InlineData("scripts/Run-DashboardBrowserScript.ps1")]
    [Xunit.InlineData("styles/app.css")]
    public void CompileNeverAddsBrowserSmokeForFrontEndChanges(string changedFile)
    {
        var policy = VerificationPolicyCompiler.Compile(
            AgentRole.Developer, "Update front-end files", string.Empty, null, [changedFile]);

        Assert.DoesNotContain(policy.Checks, c => c.Name == "dashboard browser smoke");
        Assert.DoesNotContain(policy.Checks, c => c.Kind == "browser-smoke");
    }

    [Xunit.Fact]
    public void CompileFrontEndOnlyChangeMatchesTestImpactAlone()
    {
        string[] frontEndFiles =
        [
            "src/Mcg.AgentOrchestrator.Dashboard/Program.cs", "wwwroot/app.js",
            "scripts/Run-DashboardBrowserScript.ps1", "styles/app.css"
        ];
        RepositoryTestImpactPlan[] impacts =
        [
            RepositoryTestImpactPlanner.Plan(frontEndFiles),
            RepositoryTestImpactPlanner.Plan(["docs/operator.md"])
        ];

        foreach (var impact in impacts)
        {
            var frontEndPolicy = VerificationPolicyCompiler.Compile(
                AgentRole.Developer, string.Empty, string.Empty, null, frontEndFiles, impact);
            var impactOnlyPolicy = VerificationPolicyCompiler.Compile(
                AgentRole.Developer, string.Empty, string.Empty, null, [], impact);

            Assert.Equal(impactOnlyPolicy.RequiresTests, frontEndPolicy.RequiresTests);
            Assert.Equal(
                impactOnlyPolicy.Checks.Where(c => c.Kind == "dotnet-test").OrderBy(c => c.Name).ToArray(),
                frontEndPolicy.Checks.Where(c => c.Kind == "dotnet-test").OrderBy(c => c.Name).ToArray());
        }
    }
}
