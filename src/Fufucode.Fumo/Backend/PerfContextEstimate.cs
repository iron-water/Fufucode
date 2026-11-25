using System.Collections.Generic;
using System.Linq;
using Fufucode.Core.Diagnostics;

namespace Fufucode.Core.Backend;

/// <summary>对实际即将发送的消息与工具定义做文本估算；不把估算计入模型报告的用量。</summary>
internal static class PerfContextEstimate
{
    public static void Record(PureModelRequest request)
    {
        if (!PerfTracer.Enabled) return;
        using var span = PerfTracer.Step("context.snapshot", "model", request.ModelId);
        long system = 0, chat = 0, results = 0, tools = 0;
        foreach (var message in request.Messages)
        {
            var tokens = PerfTracer.TokensOf(message.Content) + PerfTracer.TokensOf(message.ReasoningContent);
            if (message.Role == PureAgentRole.System) system += tokens;
            else if (message.Role == PureAgentRole.Tool) results += tokens;
            else chat += tokens;
            if (message.ToolCalls is not { } calls) continue;
            foreach (var call in calls)
                chat += PerfTracer.TokensOf(call.Name) + PerfTracer.TokensOf(call.ArgumentsJson);
        }
        var definitions = (request.ToolDefinitions ?? [])
            .GroupBy(definition => definition.Name, System.StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), System.StringComparer.Ordinal);
        foreach (var name in request.ToolNames.Distinct(System.StringComparer.Ordinal))
        {
            tools += PerfTracer.TokensOf(name);
            if (!definitions.TryGetValue(name, out var definition)) continue;
            tools += PerfTracer.TokensOf(definition.Description) + PerfTracer.TokensOf(definition.ParametersJsonSchema);
        }
        span.Tag("systemTokens", system);
        span.Tag("chatTokens", chat);
        span.Tag("resultTokens", results);
        span.Tag("toolTokens", tools);
        span.Tag("estimatedPromptTokens", system + chat + results + tools);
        span.Tag("source", "request-text-estimate");
    }
}
