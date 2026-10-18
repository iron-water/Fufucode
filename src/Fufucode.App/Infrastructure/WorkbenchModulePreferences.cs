using System;
using System.Text.Json;
using Fufucode.App.Infrastructure.Browser;

namespace Fufucode.App.Infrastructure;

internal sealed record WorkbenchModulePreferences(int Version = 1, bool BrowserEnabled = true, BrowserPageState? BrowserState = null, bool LibraryEnabled = true,
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
            if (state.Version != 1) throw new JsonException("Unsupported module settings version: " + state.Version);
            return state;
        }
        catch (JsonException error)
        {
            diagnostic = "模块设置无法读取，已使用默认值：" + error.Message;
            return new();
        }
    }
}
