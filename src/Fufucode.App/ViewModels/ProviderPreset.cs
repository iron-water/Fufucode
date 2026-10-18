using System;
using System.Collections.Generic;
using System.Linq;

namespace Fufucode.App.ViewModels;

/// <summary>
/// 内置提供商预设：名称、Base URL、密钥环境变量。
/// 添加提供商时先选一个预设（或"自定义"），再拉取该端点的模型。
/// 不做 OAuth：所有预设都走标准 OpenAI 兼容的 API Key 方式。
/// </summary>
public sealed record ProviderPreset(
    string Id,
    string Name,
    string BaseUrl,
    string ApiKeyEnvironmentVariable,
    string Description,
    bool RequiresKey)
{
    /// <summary>分组标题。</summary>
    public string Category { get; init; } = "API";

    public static readonly IReadOnlyList<ProviderPreset> All =
    [
        // ── 本地 ──
        new("magpie", "Magpie", "http://127.0.0.1:3425/v1", "MAGPIE_API_KEY",
            "本机 Magpie 聚合网关", RequiresKey: false) { Category = "本地" },
        new("ollama", "Ollama", "http://127.0.0.1:11434/v1", "OLLAMA_API_KEY",
            "本机 Ollama，离线可用", RequiresKey: false) { Category = "本地" },
        new("lmstudio", "LM Studio", "http://127.0.0.1:1234/v1", "LMSTUDIO_API_KEY",
            "本机 LM Studio 服务", RequiresKey: false) { Category = "本地" },
        new("vllm", "vLLM", "http://127.0.0.1:8000/v1", "VLLM_API_KEY",
            "本机 vLLM 推理服务", RequiresKey: false) { Category = "本地" },

        // ── 官方 API ──
        new("deepseek", "DeepSeek", "https://api.deepseek.com/v1", "DEEPSEEK_API_KEY",
            "DeepSeek 官方接入", RequiresKey: true),
        new("openai", "OpenAI", "https://api.openai.com/v1", "OPENAI_API_KEY",
            "OpenAI 官方接入", RequiresKey: true),
        new("anthropic", "Anthropic", "https://api.anthropic.com/v1", "ANTHROPIC_API_KEY",
            "Anthropic 官方接入", RequiresKey: true),
        new("moonshot", "Moonshot", "https://api.moonshot.cn/v1", "MOONSHOT_API_KEY",
            "月之暗面官方接入", RequiresKey: true),
        new("zhipu", "智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", "ZHIPU_API_KEY",
            "智谱官方接入，GLM 系列", RequiresKey: true),
        new("dashscope", "阿里云百炼", "https://dashscope.aliyuncs.com/compatible-mode/v1", "DASHSCOPE_API_KEY",
            "通义千问 Qwen 系列", RequiresKey: true),
        new("xai", "xAI", "https://api.x.ai/v1", "XAI_API_KEY",
            "Grok 系列模型", RequiresKey: true),
        new("mistral", "Mistral", "https://api.mistral.ai/v1", "MISTRAL_API_KEY",
            "Mistral 官方接入", RequiresKey: true),
        new("siliconflow", "SiliconFlow", "https://api.siliconflow.cn/v1", "SILICONFLOW_API_KEY",
            "硅基流动多模型 API", RequiresKey: true),

        // ── 自定义 ──
        new("custom", "自定义提供商", "", "CUSTOM_API_KEY",
            "任意 OpenAI 兼容端点", RequiresKey: false) { Category = "自定义" },
    ];

    public static ProviderPreset? Find(string? id) =>
        id is null ? null : All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));

    /// <summary>按分类分组，供选择列表展示。</summary>
    public static IReadOnlyList<IGrouping<string, ProviderPreset>> Grouped() =>
        All.GroupBy(preset => preset.Category).ToArray();
}
