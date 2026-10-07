using Mcg.AgentOrchestrator.App.Orchestration;

// Pure string inputs; no shared resources or process state.
public sealed class PowerShellClixmlStandardErrorTests
{
    private const string Header = "#< CLIXML\r\n";
    private const string Open = "<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\">";
    private const string Progress = "<Obj S=\"progress\"><MS><S N=\"AV\">Preparing modules for first use.</S></MS></Obj>";

    [Xunit.Fact]
    public void ProgressOnly_WithTwoModulePreparationRecords_ReturnsEmpty()
    {
        var input = Header + Open + Progress + Progress + "</Objs>\r\n";
        Assert.Equal("", PowerShellClixmlStandardError.Readable(input));
    }

    [Xunit.Fact]
    public void Error_WithProgressAndEscapedLineBreaks_ReturnsReadableText()
    {
        var input = Header + Open + Progress + "<S S=\"Error\">Access is denied_x000D__x000A_</S></Objs>";
        Assert.Equal("Access is denied", PowerShellClixmlStandardError.Readable(input));
    }

    [Xunit.Fact]
    public void Errors_WithAdjacentRecords_ReturnsDocumentOrderJoinedByLf()
    {
        var input = Header + Open + "<S S=\"Error\">first_x000D__x000A_</S>" +
            "<S S=\"Error\">second_x000d__x000a_</S></Objs>";
        Assert.Equal("first\nsecond", PowerShellClixmlStandardError.Readable(input));
    }

    [Xunit.Fact]
    public void PlainPrefix_BeforeHeader_IsKeptVerbatim()
    {
        var input = "plain line\r\n" + Header + Open + "<S S=\"Error\">Access is denied</S></Objs>";
        Assert.Equal("plain line\r\nAccess is denied", PowerShellClixmlStandardError.Readable(input));
        Assert.Equal("plain line\r\n", PowerShellClixmlStandardError.Readable(
            "plain line\r\n" + Header + Open + Progress + "</Objs>\r\n"));
    }

    [Xunit.Theory]
    [Xunit.InlineData(0)]
    [Xunit.InlineData(2200)]
    public void PlainStderr_WithoutHeader_IsUnchanged(int length)
    {
        var input = length == 0 ? "plain stderr\r\n" : new string('e', length);
        Assert.Equal(input, PowerShellClixmlStandardError.Readable(input));
    }

    [Xunit.Theory]
    [Xunit.InlineData("<Objs><Obj S=\"progress\">")]
    [Xunit.InlineData("<Objs")]
    [Xunit.InlineData("<Objs><S S=\"Error\">broken</Objs>")]
    [Xunit.InlineData("<Objs><S S=\"Error\">bad & text</S></Objs>")]
    [Xunit.InlineData("<!DOCTYPE Objs [<!ENTITY error 'hidden'>]><Objs><S S=\"Error\">&error;</S></Objs>")]
    [Xunit.InlineData("<Objs><!DOCTYPE Objs><S S=\"Error\">error</S></Objs>")]
    public void UnreadableDocument_AfterHeader_ReturnsEntireOriginal(string body)
    {
        var input = "plain line\r\n" + Header + body;
        Assert.Equal(input, PowerShellClixmlStandardError.Readable(input));
    }

    [Xunit.Fact]
    public void MultipleDocuments_WithInterveningPlainText_AreReplacedInPlace()
    {
        var input = Header + "<Objs><S S=\"Error\">first</S></Objs>\nplain\n" +
            "<Objs><S S=\"Error\">second</S></Objs> suffix";
        Assert.Equal("first\nplain\nsecond suffix", PowerShellClixmlStandardError.Readable(input));
    }

    [Xunit.Fact]
    public void CommentOutsideDocument_WithRootText_IsPreserved()
    {
        const string comment = "<!-- plain <Objs> text -->";
        var input = Header + comment + "<Objs><S S=\"Error\">error</S></Objs>" + comment;
        Assert.Equal(comment + "error" + comment, PowerShellClixmlStandardError.Readable(input));
    }

    [Xunit.Fact]
    public void SecondDocument_WhenIncomplete_RestoresEntireInput()
    {
        var input = Header + "<Objs><S S=\"Error\">first</S></Objs>\n<Objs>";
        Assert.Equal(input, PowerShellClixmlStandardError.Readable(input));
    }

    [Xunit.Fact]
    public void NestedRecords_WithPrefixesAndEscapes_KeepOnlyErrors()
    {
        var input = Header + "<p:Objs xmlns:p=\"urn:test\"><p:Objs note=\"quoted > text\">" +
            "<!-- </p:Objs> --><p:S S=\"Error\">A_x0026_B_x005F_x000D_</p:S>" +
            "<p:S S=\"Warning\">omit</p:S><p:S S=\"Error\"><![CDATA[</p:Objs>]]></p:S>" +
            "</p:Objs></p:Objs>";
        Assert.Equal("A&B_x000D_\n</p:Objs>", PowerShellClixmlStandardError.Readable(input));
    }

    [Xunit.Fact]
    public void Header_InsidePlainLine_IsNotRecognized()
    {
        const string input = "plain #< CLIXML\n<Objs/>";
        Assert.Equal(input, PowerShellClixmlStandardError.Readable(input));
    }

    [Xunit.Fact]
    public void EmptyDocument_WithLfHeader_ReturnsEmpty()
    {
        Assert.Equal("", PowerShellClixmlStandardError.Readable("#< CLIXML\n<Objs/>"));
    }
}
