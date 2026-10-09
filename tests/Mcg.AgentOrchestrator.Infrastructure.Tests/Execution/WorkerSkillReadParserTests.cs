using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: pure parsers and in-memory fixtures.
public sealed class WorkerSkillReadParserTests
{
    internal const string CodexEvents = """
        {"type":"item.started","item":{"id":"item_1","type":"command_execution","command":"pwsh -Command 'Get-Content .agents/skills/verification-before-completion/SKILL.md; git status --short'","aggregated_output":"","exit_code":null,"status":"in_progress"}}
        {"type":"item.started","item":{"id":"item_2","type":"command_execution","command":"pwsh -Command 'Get-Content .agents\\skills\\orchestrator-worker-verification\\SKILL.md'","aggregated_output":"","exit_code":null,"status":"in_progress"}}
        {"type":"item.completed","item":{"id":"item_3","type":"agent_message","text":"I read .agents/skills/systematic-debugging/SKILL.md"}}
        """;

    [Fact]
    public void CodexReadsOnlyStartedCommands()
    {
        var result = WorkerSkillReadParser.ParseCodexEvents(CodexEvents);
        Assert.Equal(["verification-before-completion", "orchestrator-worker-verification"], result.Skills);
        Assert.Equal(0, result.MalformedLineCount);
    }

    [Fact]
    public void ClaudeReadsOnlyReadAndBashToolUseAndCountsMalformedLines()
    {
        const string text = """
            {"type":"assistant","message":{"id":"m1","content":[{"type":"tool_use","id":"t1","name":"Read","input":{"file_path":"C:\\repo\\.agents\\skills\\criterion-ownership-planning\\SKILL.md"}}]}}
            {"type":"assistant","message":{"id":"m2","content":[{"type":"tool_use","id":"t2","name":"Bash","input":{"command":"cat .agents/skills/research-evidence/SKILL.md"}},{"type":"text","text":"done"}]}}
            {"type":"user","message":{"content":[{"type":"tool_result","content":"see .agents/skills/skill-authoring/SKILL.md"}]}}
            {"type":"assistant","message":
            """;
        var result = WorkerSkillReadParser.ParseClaudeTranscript(text);
        Assert.Equal(["criterion-ownership-planning", "research-evidence"], result.Skills);
        Assert.Equal(1, result.MalformedLineCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HandlesWrongJsonShapesCaseDuplicatePathsAndBlankLines(bool claude)
    {
        const string target = ".AGENTS/SKILLS/Research-Evidence/skill.MD .agents/skills/research-evidence/SKILL.md";
        var entry = claude
            ? System.Text.Json.JsonSerializer.Serialize(new { type = "assistant", message = new { content = new[] {
                new { type = "tool_use", name = "Read", input = new { file_path = target } } } } })
            : System.Text.Json.JsonSerializer.Serialize(new { type = "item.started", item = new {
                type = "command_execution", command = target } });
        var text = "\nnull\n[]\n42\n{\"type\":\"assistant\",\"message\":{\"content\":[null,1]}}\n{bad\n" + entry;
        var result = claude ? WorkerSkillReadParser.ParseClaudeTranscript(text) : WorkerSkillReadParser.ParseCodexEvents(text);
        Assert.Equal(["research-evidence"], result.Skills);
        Assert.Equal(1, result.MalformedLineCount);
    }
}
