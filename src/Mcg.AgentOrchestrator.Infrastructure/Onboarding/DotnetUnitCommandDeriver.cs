using Mcg.AgentOrchestrator.Core;
using System.Xml;
using System.Xml.Linq;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class DotnetUnitCommandDeriver
{
    public static UnitCommands Derive(string id, XDocument? document, ProjectFact<bool?> isTest,
        ProjectFact<string>? runner, List<ProjectOwnerQuestion> questions)
    {
        var project = document?.Root;
        var sdkDeclared = project?.Attribute("Sdk") is { Value.Length: > 0 } ||
            project?.Elements().Any(element => element.Name.LocalName == "Sdk" ||
                element.Name.LocalName == "Import" && element.Attribute("Sdk") is not null) == true;
        var source = new FactSource(id, Math.Max(1, (project as IXmlLineInfo)?.LineNumber ?? 1));
        var path = id.Any(char.IsWhiteSpace) ? $"\"{id}\"" : id;
        var build = DotnetProjectDiscoveryAdapter.Fact($"dotnet build {path}",
            sdkDeclared ? FactConfidence.High : FactConfidence.Low, source,
            $"commands/{id}/build", "Confirm the build command; no parsed SDK declaration establishes it.", questions);
        ProjectFact<string>? test = null;
        if (isTest.Value != false)
        {
            var command = isTest.Value == true ? runner?.Value switch
            {
                "VSTest" => $"dotnet test {path} --no-build",
                "MTP" => $"dotnet run --project {path} --no-build --property:UseAppHost=false",
                _ => "undetermined"
            } : "undetermined";
            var confidence = command == "undetermined" ? FactConfidence.Low
                : (FactConfidence)Math.Max((int)isTest.Confidence, (int)runner!.Confidence);
            test = DotnetProjectDiscoveryAdapter.Fact(command, confidence, runner?.Source ?? isTest.Source,
                $"commands/{id}/test", "Confirm the test command; test status or runner is uncertain.", questions);
        }
        return new UnitCommands(id, build, test);
    }
}
