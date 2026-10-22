using System;
using System.Text.Json;
using Fufucode.App.Infrastructure.Browser;

namespace Fufucode.App.Infrastructure;

internal sealed record WorkbenchModulePreferences(int Version = 2, bool BrowserEnabled = false, BrowserPageState? BrowserState = null, bool LibraryEnabled = true,
    bool LocalReviewEnabled = true, bool AutomationEnabled = true, string? AutomationDatabase = null)
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
            // v1 时浏览器页是默认开启的内置模块；新版本把它降为默认不安装。
            // 只迁移一次，保留其它模块选择，避免用户已有布局整体重置。
            if (state.Version == 1)
                return new WorkbenchModulePreferences(Version: 2, BrowserEnabled: false,
                    BrowserState: state.BrowserState, LibraryEnabled: state.LibraryEnabled,
                    LocalReviewEnabled: state.LocalReviewEnabled, AutomationEnabled: state.AutomationEnabled,
                    AutomationDatabase: state.AutomationDatabase);
            if (state.Version != 2) throw new JsonException("Unsupported module settings version: " + state.Version);
            return state;
        }
        catch (JsonException error)
        {
            diagnostic = "模块设置无法读取，已使用默认值：" + error.Message;
            return new();
        }
    }
}
