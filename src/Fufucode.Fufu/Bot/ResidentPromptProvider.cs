using Fufucode.Core.Plugins;

namespace Fufucode.Fufu.Bot;

/// <summary>每轮读取 Resident Core，保持记忆更新后无需重建 FUMO 会话。</summary>
public sealed class ResidentPromptProvider : IPluginSystemPromptProvider
{
    private readonly ResidentCore _resident;

    public ResidentPromptProvider(ResidentCore resident) => _resident = resident;

    public string? Current => _resident.BuildPromptSection();
}
