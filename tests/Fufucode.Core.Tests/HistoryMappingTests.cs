using System.Text.Json;
using Fufucode.App.Abstractions;
using Fufucode.Core;
using Fufucode.Pi;

public static class HistoryMappingTests
{
    public static void Run()
    {
        using var userJson = JsonDocument.Parse("""{"role":"user","content":[{"type":"text","text":"question"}]}""");
        using var assistantJson = JsonDocument.Parse("""{"role":"assistant","model":"example-provider/example-model","stopReason":"aborted","content":[{"type":"thinking","thinking":"reason"},{"type":"toolCall","id":"call-1","name":"write","arguments":{"path":"a.txt"}},{"type":"custom","value":"ignored"}],"usage":{"input":2147483648,"output":7,"cacheRead":3,"cacheWrite":2,"cost":{"total":0.125}}}""");
        using var toolJson = JsonDocument.Parse("""{"role":"toolResult","content":[{"type":"text","text":"result"}]}""");
        var messages = new[]
        {
            PiProtocol.ParseMessage(userJson.RootElement),
            PiProtocol.ParseMessage(assistantJson.RootElement),
            PiProtocol.ParseMessage(toolJson.RootElement),
        };

        var transcript = MessageBlockMapper.MapTranscript(messages, "fallback/model");
        if (transcript.Count != 2) throw new Exception("History mapper must omit toolResult entries.");
        if (transcript[0] is not { Id: "history-1", Role: MessageRole.User, Model: null, IsComplete: true } ||
            transcript[0].Blocks.Single() is not TextBlock { Text: "question" })
            throw new Exception("History mapper must preserve the user message shape.");

        var assistant = transcript[1];
        if (assistant.Id != "history-2" || assistant.Role != MessageRole.Assistant ||
            assistant.Model != "example-provider/example-model" || assistant.Blocks.Count != 2)
            throw new Exception("History mapper must retain assistant model and supported content only.");
        if (assistant.Blocks[0] is not ThinkingBlock { Text: "reason" } ||
            assistant.Blocks[1] is not ToolCallBlock
            {
                ToolCallId: "call-1", ToolName: "write", InputJson: "{\"path\":\"a.txt\"}", Status: ToolCallStatus.Cancelled
            })
            throw new Exception("History mapper must preserve thinking and classify aborted calls as cancelled.");
        if (assistant.CumulativeUsage is not
            { InputTokens: null, OutputTokens: 7, CacheReadTokens: 3, CacheWriteTokens: 2, TotalCost: 0.125m })
            throw new Exception("History mapper must preserve bounded usage and total cost.");

        using var failedJson = JsonDocument.Parse("""{"role":"assistant","stopReason":"error","content":[{"type":"toolCall","id":"call-2","name":"read","arguments":{}}]}""");
        var failed = MessageBlockMapper.MapMessage(PiProtocol.ParseMessage(failedJson.RootElement), "fallback/model", "failed");
        if (failed?.Model != "fallback/model" || failed.Blocks.Single() is not ToolCallBlock { Status: ToolCallStatus.Failed })
            throw new Exception("History mapper must use current model fallback and preserve failed tool status.");

        Console.WriteLine("PASS persisted transcript MessageBlockMapper mapping");
    }
}
