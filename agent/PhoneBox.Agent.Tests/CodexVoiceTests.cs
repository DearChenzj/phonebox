using System.Text.Json;
using Xunit;

namespace PhoneBox.Agent.Tests;

public class CodexVoiceTests
{
    [Fact]
    public void RecentVoiceContextPreservesMessageOrderAndExcludesNonConversationItems()
    {
        using var turns = JsonDocument.Parse("""
            {"data":[
                {"items":[
                    {"type":"userMessage","content":[{"type":"text","text":"Second question"}]},
                    {"type":"agentMessage","phase":"commentary","text":"Working"},
                    {"type":"agentMessage","phase":"final","text":"Second answer"}
                ]},
                {"items":[
                    {"type":"userMessage","content":[{"type":"image","url":"image"},{"type":"text","text":"First question"}]},
                    {"type":"commandExecution","aggregatedOutput":"Shell output"},
                    {"type":"agentMessage","phase":null,"text":"First answer"}
                ]}
            ]}
            """);

        Assert.Equal(
            [
                new CodexVoice.VoiceMessage("user", "First question"),
                new CodexVoice.VoiceMessage("assistant", "First answer"),
                new CodexVoice.VoiceMessage("user", "Second question"),
                new CodexVoice.VoiceMessage("assistant", "Second answer"),
            ],
            CodexVoice.GetRecentMessages(turns.RootElement));
    }
}
