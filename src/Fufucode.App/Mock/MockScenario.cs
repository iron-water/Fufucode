using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.App.Abstractions;

namespace Fufucode.App.Mock;

/// <summary>发射一个会话事件。</summary>
internal delegate void EmitEvent(SessionEvent e);

/// <summary>可取消的延时（模拟 delta 节奏）。</summary>
internal delegate Task DelayAsync(int milliseconds, CancellationToken cancellationToken);

/// <summary>
/// mock 生成器：用脚本化的事件序列模拟 Pi 的流式输出节奏——思考块、文本增量、
/// 工具调用（含 JSON 分片与失败态）、最终消息整体替换与累计用量。
/// 不访问任何真实进程；内容仅用于演示与界面自测。
/// </summary>
internal static class MockScenario
{
    public const string Model = "Mock Pro 1（演示）";

    private const int TextDeltaDelayMs = 24;
    private const int ThinkingDeltaDelayMs = 45;
    private const int ToolExecDelayMs = 650;

    /// <summary>
    /// 正常演示回合：思考 → 文本（含代码块）→ 工具调用成功 → 文本 → 工具调用失败 → 收尾。
    /// 返回值是该回合的最终内容块，供调用方发射整体替换用的 <see cref="SessionEvent.MessageFinished"/>。
    /// </summary>
    public static async Task<IReadOnlyList<MessageBlock>> PlayAsync(
        string userText, EmitEvent emit, DelayAsync delay, string messageId, CancellationToken ct)
    {
        var finalBlocks = new List<MessageBlock>();

        const int blockCount = 6;
        emit(new SessionEvent.MessageStarted(messageId, MessageRole.Assistant, Model, blockCount));

        // --- 可折叠思考块 ---
        const string thinking = "用户想了解项目情况。我先确认目录结构，定位 README.md，" +
                                "再决定要不要先构建一次验证当前状态，最后组织回答。";
        emit(new SessionEvent.BlockStarted(messageId, 0, MessageBlockKind.Thinking, null, null));
        foreach (var chunk in ChunkText(thinking))
        {
            emit(new SessionEvent.ThinkingAppended(messageId, 0, chunk));
            await delay(ThinkingDeltaDelayMs, ct);
        }
        finalBlocks.Add(new ThinkingBlock(thinking));
        emit(new SessionEvent.BlockFinished(messageId, 0, finalBlocks[^1]));

        // --- 文本块（含代码块，演示代码块渲染与复制） ---
        const string intro = "我先看一下工程结构：\n\n```text\nFufucode/\n  src/\n    Fufucode.App/\n  docs/\n```\n\n接下来读取 README.md。";
        emit(new SessionEvent.BlockStarted(messageId, 1, MessageBlockKind.Text, null, null));
        foreach (var chunk in ChunkText(intro))
        {
            emit(new SessionEvent.TextAppended(messageId, 1, chunk));
            await delay(TextDeltaDelayMs, ct);
        }
        finalBlocks.Add(new TextBlock(intro));
        emit(new SessionEvent.BlockFinished(messageId, 1, finalBlocks[^1]));

        // --- 工具调用：参数 JSON 分片到达（演示未完成 JSON 的临时展示），随后成功 ---
        const string toolCallId1 = "call-readme";
        const string inputJson1 = "{\"path\":\"README.md\"}";
        emit(new SessionEvent.BlockStarted(messageId, 2, MessageBlockKind.ToolCall, toolCallId1, "read_file"));
        emit(new SessionEvent.ToolCallAppended(messageId, 2, toolCallId1, "{\"path\":\"READM"));
        await delay(TextDeltaDelayMs * 2, ct);
        emit(new SessionEvent.ToolCallAppended(messageId, 2, toolCallId1, "E.md\"}"));
        emit(new SessionEvent.ToolCallStatusChanged(messageId, 2, toolCallId1, ToolCallStatus.Running, "读取 README.md"));
        await delay(ToolExecDelayMs, ct);
        const string readmeOutput =
            "# Fufucode\n\n基于 Pi 官方 RPC 的 Windows 优先 Avalonia coding agent 客户端。\n\n" +
            "- 不移植 Agent 内核，全部由 Pi 执行\n- 桌面只负责消息时间线、工具卡片与审批入口\n" +
            "- 详细计划见 docs/AI_DEVELOPMENT_PLAN.md\n（mock 演示输出，非真实文件内容）";
        var readmeBlock = new ToolCallBlock(toolCallId1, "read_file", inputJson1,
            ToolCallStatus.Completed, "已读取 7 行", readmeOutput, false);
        finalBlocks.Add(readmeBlock);
        emit(new SessionEvent.BlockFinished(messageId, 2, readmeBlock));

        // --- 文本块：总结 ---
        const string summary = "README 的要点：这是一个通过官方 RPC 驱动 Pi 的 Avalonia 客户端，" +
                               "桌面不实现 Agent 内核。完整链路要等 M2 集成真实 Core 之后才能跑通。\n\n" +
                               "顺便演示一次构建命令的失败态：";
        emit(new SessionEvent.BlockStarted(messageId, 3, MessageBlockKind.Text, null, null));
        foreach (var chunk in ChunkText(summary))
        {
            emit(new SessionEvent.TextAppended(messageId, 3, chunk));
            await delay(TextDeltaDelayMs, ct);
        }
        finalBlocks.Add(new TextBlock(summary));
        emit(new SessionEvent.BlockFinished(messageId, 3, finalBlocks[^1]));

        // --- 工具调用：演示失败态（非零退出码） ---
        const string toolCallId2 = "call-build";
        const string inputJson2 = "{\"command\":\"dotnet build src/Fufucode.App/Fufucode.App.csproj\"}";
        emit(new SessionEvent.BlockStarted(messageId, 4, MessageBlockKind.ToolCall, toolCallId2, "bash"));
        emit(new SessionEvent.ToolCallAppended(messageId, 4, toolCallId2, "{\"command\":\"dotnet build "));
        await delay(TextDeltaDelayMs * 2, ct);
        emit(new SessionEvent.ToolCallAppended(messageId, 4, toolCallId2, "src/Fufucode.App/Fufucode.App.csproj\"}"));
        emit(new SessionEvent.ToolCallStatusChanged(messageId, 4, toolCallId2, ToolCallStatus.Running, "运行构建"));
        await delay(ToolExecDelayMs, ct);
        const string buildOutput =
            "MSBuild version 17.x\n  已确定要还原的项目…\n" +
            "src/Fufucode.App/Fufucode.App.csproj(1,1): error MOCK001: 这是一个 mock 演示的失败输出（退出码 1）\n\n生成失败。";
        var buildBlock = new ToolCallBlock(toolCallId2, "bash", inputJson2,
            ToolCallStatus.Failed, "退出码 1", buildOutput, true);
        finalBlocks.Add(buildBlock);
        emit(new SessionEvent.BlockFinished(messageId, 4, buildBlock));

        // --- 收尾文本 ---
        const string closing = "构建报错了——这是 mock 演示的失败态，真实环境下我会读取错误日志并修复。";
        const int closingIndex = 5;
        emit(new SessionEvent.BlockStarted(messageId, closingIndex, MessageBlockKind.Text, null, null));
        foreach (var chunk in ChunkText(closing))
        {
            emit(new SessionEvent.TextAppended(messageId, closingIndex, chunk));
            await delay(TextDeltaDelayMs, ct);
        }
        finalBlocks.Add(new TextBlock(closing));
        emit(new SessionEvent.BlockFinished(messageId, closingIndex, finalBlocks[^1]));

        return finalBlocks;
    }

    /// <summary>失败演示回合：输出到一半时报模型错误，演示错误条目与未完成消息残留（不产生最终消息）。</summary>
    public static async Task PlayFailureAsync(EmitEvent emit, DelayAsync delay, string messageId, CancellationToken ct)
    {
        const string partial = "正在回答，但";
        emit(new SessionEvent.MessageStarted(messageId, MessageRole.Assistant, Model, 1));
        emit(new SessionEvent.BlockStarted(messageId, 0, MessageBlockKind.Text, null, null));
        foreach (var chunk in ChunkText(partial))
        {
            emit(new SessionEvent.TextAppended(messageId, 0, chunk));
            await delay(TextDeltaDelayMs, ct);
        }
        await delay(ToolExecDelayMs, ct);
        // 没有 BlockFinished / MessageFinished：未完成消息按原样残留
    }

    /// <summary>把文本拆成模拟流式节奏的小片（CJK 每片 2-3 字，拉丁文按词）。</summary>
    public static IEnumerable<string> ChunkText(string text)
    {
        var buffer = new List<char>();
        foreach (var ch in text)
        {
            buffer.Add(ch);
            if (ch == '\n' || ch == ' ' || ch == '。' || ch == '，' || ch == '：' || ch == '\r')
            {
                yield return new string(buffer.ToArray());
                buffer.Clear();
                continue;
            }
            if (buffer.Count >= 3)
            {
                yield return new string(buffer.ToArray());
                buffer.Clear();
            }
        }
        if (buffer.Count > 0)
        {
            yield return new string(buffer.ToArray());
        }
    }
}
