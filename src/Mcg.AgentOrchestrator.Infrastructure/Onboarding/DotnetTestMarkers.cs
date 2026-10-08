using System.Xml.Linq;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>The test declarations in a project's own XML, independent of their reliability.</summary>
internal static class DotnetTestMarkers
{
    internal static readonly string[] RunnerProperties =
        ["UseMicrosoftTestingPlatformRunner", "EnableMSTestRunner", "EnableNUnitRunner", "IsTestingPlatformApplication"];

    internal static bool IsMarkerFree(XDocument project) =>
        !project.Descendants().Any(element =>
            element.Name.LocalName == "IsTestProject" ||
            RunnerProperties.Contains(element.Name.LocalName, StringComparer.Ordinal) ||
            element.Name.LocalName == "PackageReference" &&
                IsTestPackage(element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value ?? "") ||
            element.Name.LocalName == "Sdk" && IsTestSdk(element.Attribute("Name")?.Value ?? "") ||
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
        FrameworkName(package) is not null || IsTestSdkPackage(package) || IsVstestAdapter(package);

    private static bool IsTestSdk(string sdk) =>
        sdk.Trim().StartsWith("MSTest.Sdk", StringComparison.OrdinalIgnoreCase);
}
