using System;
using System.Linq;
using Fufucode.Core.Memory;

namespace Fufucode.App.ViewModels;

/// <summary>设置 → 其他 → 记忆：tool.memory 插件与 MEMORY.md 条目。</summary>
public sealed partial class SettingsPageViewModel
{
    public bool ShowAgentMemory => SelectedCategory?.Id == "memory";
    public MemorySettingsViewModel Memory { get; } = new();

    private void AttachMemory(Context context) =>
        Memory.Attach(context.DataRoot,
            () => MarketplacePlugins.FirstOrDefault(item => string.Equals(item.Id, MemoryNotebook.PluginId, StringComparison.OrdinalIgnoreCase)),
            InstallOrUninstallPlugin, TogglePluginEnabled, path => _ = context.OpenFolder(path));
}
