using System;
using System.Collections.Generic;
using System.Linq;

namespace Fufucode.Core.Backend;

/// <summary>一种模型线协议。Id 与 Pi models.json 的 <c>api</c> 字段同名，两边配置可以互通。</summary>
public sealed record ModelApiKind(string Id, string DisplayName);

/// <summary>
/// FUMO 原生支持的模型协议目录（唯一真源）：设置校验、设置页选项与客户端工厂都从这里读。
/// 未填写时按 OpenAI Chat Completions 处理，旧配置行为不变。
/// </summary>
public static class ModelApi
{
    public const string OpenAiCompletions = "openai-completions";
    public const string OpenAiResponses = "openai-responses";
    public const string AnthropicMessages = "anthropic-messages";

    public static readonly IReadOnlyList<ModelApiKind> All =
    [
        new(OpenAiCompletions, "OpenAI Chat Completions"),
        new(OpenAiResponses, "OpenAI Responses"),
        new(AnthropicMessages, "Anthropic Messages"),
    ];

    public static ModelApiKind? Find(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null :
        All.FirstOrDefault(kind => string.Equals(kind.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>空值视为 Chat Completions；未知值原样返回，由调用方如实报错。</summary>
    public static string Normalize(string? id) =>
        string.IsNullOrWhiteSpace(id) ? OpenAiCompletions : Find(id)?.Id ?? id.Trim();

    /// <summary>按协议创建原生客户端。未知协议抛出，不悄悄退回 Chat Completions。</summary>
    public static IPureModelClient CreateClient(string? api, string baseUrl, string? apiKey, string defaultModelId) =>
        Normalize(api) switch
        {
            OpenAiCompletions => new OpenAiCompatibleModelClient(baseUrl, apiKey, defaultModelId),
            OpenAiResponses => new OpenAiResponsesModelClient(baseUrl, apiKey, defaultModelId),
            AnthropicMessages => new AnthropicMessagesModelClient(baseUrl, apiKey, defaultModelId),
            var unknown => throw new ArgumentException($"不支持的模型协议 '{unknown}'。可用：{string.Join("、", All.Select(kind => kind.Id))}。"),
        };
}
