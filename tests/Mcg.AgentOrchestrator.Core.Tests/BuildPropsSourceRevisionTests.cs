using System.Xml.Linq;

// Parallel-safe: reads repository configuration and uses only local XML documents.
public sealed class BuildPropsSourceRevisionTests
{
    [Xunit.Fact]
    public void RepositoryPropsSourceRevisionIsDisabledUnconditionally()
    {
        var props = XDocument.Load(Path.Combine(VerifiedRepositoryRoot.Find(), "Directory.Build.props"));

        Xunit.Assert.True(IsSourceRevisionDisabledUnconditionally(props),
            "The first unconditional PropertyGroup must contain exactly one " +
            "IncludeSourceRevisionInInformationalVersion element with trimmed value 'false'.");
    }

    [Xunit.Fact]
    public void SourceRevisionOnlyConditionalPropertyIsNotSetUnconditionally()
    {
        var props = XDocument.Parse("""
            <Project>
              <PropertyGroup>
                <AnalysisLevel>latest</AnalysisLevel>
              </PropertyGroup>
              <PropertyGroup Condition="'$(Configuration)' == 'Debug'">
                <IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>
              </PropertyGroup>
            </Project>
            """);

        Xunit.Assert.False(IsSourceRevisionDisabledUnconditionally(props));
    }

    private static bool IsSourceRevisionDisabledUnconditionally(XDocument props)
    {
        if (props.Root is not { } project || project.Name.LocalName != "Project")
            return false;

        var propertyGroup = project.Elements().FirstOrDefault(element =>
            element.Name.LocalName == "PropertyGroup" && element.Attribute("Condition") is null);
        if (propertyGroup is null)
            return false;

        var properties = propertyGroup.Elements()
            .Where(element => element.Name.LocalName == "IncludeSourceRevisionInInformationalVersion")
            .ToArray();

        return properties.Length == 1 &&
            string.Equals(properties[0].Value.Trim(), "false", StringComparison.Ordinal);
    }
}
