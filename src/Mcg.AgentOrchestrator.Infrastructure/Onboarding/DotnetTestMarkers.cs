using System.Xml.Linq;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>The test declarations in a project's own XML, independent of their reliability.</summary>
internal static class DotnetTestMarkers
{
    internal static IReadOnlyList<string> RunnerProperties { get; } = Array.AsReadOnly<string>(
        ["UseMicrosoftTestingPlatformRunner", "EnableMSTestRunner", "EnableNUnitRunner", "IsTestingPlatformApplication"]);

    internal static bool IsMarkerFree(XDocument project) =>
        !project.Descendants().Any(element =>
            element.Name.LocalName.Equals("IsTestProject", StringComparison.OrdinalIgnoreCase) ||
            RunnerProperties.Contains(element.Name.LocalName, StringComparer.OrdinalIgnoreCase) ||
            element.Name.LocalName.Equals("PackageReference", StringComparison.OrdinalIgnoreCase) &&
                IsTestPackage(element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value ?? "") ||
            element.Name.LocalName.Equals("Sdk", StringComparison.OrdinalIgnoreCase) && IsTestSdk(element.Attribute("Name")?.Value ?? "") ||
            element.Attribute("Sdk") is { } sdk && sdk.Value.Split(';').Any(IsTestSdk));

    internal static string? FrameworkName(string package) => package.ToLowerInvariant() switch
    {
        "xunit" or "xunit.v3" or "xunit.v3.core" => "xUnit",
        "nunit" => "NUnit",
        "mstest.testframework" => "MSTest",
        var name when name.StartsWith("xunit.v3.mtp-", StringComparison.Ordinal) ||
            name.StartsWith("xunit.v3.core.mtp-", StringComparison.Ordinal) => "xUnit",
        _ => null
    };

    internal static bool IsTestSdkPackage(string package) =>
        package.Equals("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase);

    internal static bool IsVstestAdapter(string package) =>
        package.Equals("xunit.runner.visualstudio", StringComparison.OrdinalIgnoreCase) ||
        package.Equals("MSTest.TestAdapter", StringComparison.OrdinalIgnoreCase) ||
        package.Equals("NUnit3TestAdapter", StringComparison.OrdinalIgnoreCase);

    private static bool IsTestPackage(string package) =>
        FrameworkName(package) is not null || IsTestSdkPackage(package) || IsVstestAdapter(package) ||
        package.Equals("MSTest", StringComparison.OrdinalIgnoreCase) ||
        package.Equals("TUnit", StringComparison.OrdinalIgnoreCase) ||
        package.Equals("TUnit.Core", StringComparison.OrdinalIgnoreCase);

    private static bool IsTestSdk(string sdk) =>
        sdk.Trim().StartsWith("MSTest.Sdk", StringComparison.OrdinalIgnoreCase);
}
