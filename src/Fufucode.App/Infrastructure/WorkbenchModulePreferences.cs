using System;
using System.Text.Json;
using Fufucode.App.Infrastructure.Browser;

namespace Fufucode.App.Infrastructure;

internal sealed record WorkbenchModulePreferences(int Version = 3, bool BrowserEnabled = false, BrowserPageState? BrowserState = null, bool LibraryEnabled = true,
    bool LocalReviewEnabled = true, bool AutomationEnabled = false, string? AutomationDatabase = null)
{
    public string Serialize() => JsonSerializer.Serialize(this);

    public static WorkbenchModulePreferences Restore(string? text, out string? diagnostic)
    {
        diagnostic = null;
        if (string.IsNullOrWhiteSpace(text)) return new();
        try
        {
            var state = JsonSerializer.Deserialize<WorkbenchModulePreferences>(text)
                ?? throw new JsonException("Module settings are empty.");
            // v1/v2 时浏览器和自动开发还是默认开启的内置模块；新版本把它们降为
            // 默认不安装。只迁移一次，保留资料库/本地审查等其它选择。
            if (state.Version is 1 or 2)
                return new WorkbenchModulePreferences(Version: 3, BrowserEnabled: false,
                    BrowserState: state.BrowserState, LibraryEnabled: state.LibraryEnabled,
                    LocalReviewEnabled: state.LocalReviewEnabled, AutomationEnabled: false,
                    AutomationDatabase: state.AutomationDatabase);
            if (state.Version != 3) throw new JsonException("Unsupported module settings version: " + state.Version);
            return state;
        }
        catch (JsonException error)
        {
            diagnostic = "模块设置无法读取，已使用默认值：" + error.Message;
            return new();
        }
    }
}
