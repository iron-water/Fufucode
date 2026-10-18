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

        RunSessionHistoryBatch();
    }

    // 批量条目应用：恢复路径一次调用、只重建一次；校验去重、回滚与索引一致性。
    private static void RunSessionHistoryBatch()
    {
        using var first = JsonDocument.Parse("""
            [{"type":"message","id":"u","parentId":null,"message":{"role":"user","content":"first"}},
             {"type":"message","id":"a","parentId":"u","message":{"role":"assistant","content":"second"}}]
            """);
        using var second = JsonDocument.Parse("""
            [{"type":"message","id":"a2","parentId":"a","message":{"role":"assistant","content":"third"}},
             {"type":"message","id":"t","parentId":"a2","message":{"role":"toolResult","content":"result"}}]
            """);
        using var leafA = JsonDocument.Parse("\"a\"");
        using var leafT = JsonDocument.Parse("\"t\"");
        using var dup = JsonDocument.Parse("""[{"type":"message","id":"u","parentId":null}]""");
        using var unknownLeaf = JsonDocument.Parse("\"missing\"");
        using var replaceable = JsonDocument.Parse("""
            [{"type":"message","id":"r1","parentId":null,"message":{"role":"assistant","content":"only"}}]
            """);
        using var leafR1 = JsonDocument.Parse("\"r1\"");

        var history = new SessionHistory();
        history.ApplyEntries(first.RootElement.EnumerateArray(), leafA.RootElement, replace: true);
        if (history.Entries.Count != 2 || history.Cursor != "a" || history.LeafId != "a" ||
            history.CurrentBranchMessages.Count != 2)
            throw new Exception("Batch history apply must rebuild the first batch.");

        history.ApplyEntries(second.RootElement.EnumerateArray(), leafT.RootElement);
        if (history.Entries.Count != 4 || history.Cursor != "t" || history.LeafId != "t" ||
            history.CurrentBranchMessages.Count != 4 || !history.Entries[3].OnCurrentBranch)
            throw new Exception("Batch history apply must append entries and rebuild once.");

        // 重复 id 拒绝，且状态保持为应用前的快照。
        try
        {
            history.ApplyEntries(dup.RootElement.EnumerateArray(), leafT.RootElement);
            throw new Exception("Batch history apply accepted a repeated entry.");
        }
        catch (RpcException) { }
        if (history.Entries.Count != 4 || history.Cursor != "t" || history.LeafId != "t")
            throw new Exception("Batch history apply rollback must restore the previous snapshot.");

        // 未知叶节点：校验失败回滚；随后合法追加仍可工作（ID 索引保持一致）。
        try
        {
            history.ApplyEntries(second.RootElement.EnumerateArray(), unknownLeaf.RootElement, replace: true);
            throw new Exception("Batch history apply accepted an unknown leaf.");
        }
        catch (RpcException) { }
        if (history.Entries.Count != 4 || history.Cursor != "t")
            throw new Exception("Failed replace must restore the previous snapshot.");
        history.ApplyEntries(replaceable.RootElement.EnumerateArray(), leafR1.RootElement, replace: true);
        if (history.Entries.Count != 1 || history.LeafId != "r1" || history.CurrentBranchMessages.Count != 1)
            throw new Exception("Replace after rollback must produce exactly the new entries.");

        // API 产物：一次性写出的原子 JSON 可从文件完整读回。
        var path = Path.Combine(Path.GetTempPath(), "fufucode-atomic-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            AtomicJsonFile.Write(path, writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("version", 2);
                writer.WriteString("modelId", "m");
                writer.WriteEndObject();
            });
            using var read = JsonDocument.Parse(File.ReadAllText(path));
            if (read.RootElement.GetProperty("version").GetInt32() != 2 ||
                read.RootElement.GetProperty("modelId").GetString() != "m")
                throw new Exception("Atomic JSON write must produce a readable document.");
            if (Directory.EnumerateFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".tmp-*").Any())
                throw new Exception("Atomic JSON write must not leave temporary files behind.");
        }
        finally
        {
            File.Delete(path);
        }

        // 模型能力缓存有硬上限：大量不同键也不无限增长。
        Fufucode.Core.Backend.ModelCapabilityProbe.ClearCache();
        for (var i = 0; i < Fufucode.Core.Backend.ModelCapabilityProbe.MaxCacheEntries + 32; i++)
            Fufucode.Core.Backend.ModelCapabilityProbe.Remember("test|" + i, true);
        if (Fufucode.Core.Backend.ModelCapabilityProbe.CacheCount >
            Fufucode.Core.Backend.ModelCapabilityProbe.MaxCacheEntries)
            throw new Exception("Model capability cache must evict entries beyond its cap.");

        Console.WriteLine("PASS session history batch apply, atomic JSON write and bounded capability cache");
    }
}
