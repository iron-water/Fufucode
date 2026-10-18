namespace Fufucode.App.Features.BrowserExperience;

/// <summary>
/// 对标 Codex / ChatGPT 的标准 Agent Browser Use 动作契约。
/// </summary>
public static class BrowserUseActionNames
{
    public const string Navigate = "browser_navigate";
    public const string Click = "browser_click";
    public const string Type = "browser_type";
    public const string Screenshot = "browser_screenshot";
    public const string GetDom = "browser_get_dom";
    public const string GetState = "browser_get_state";
    public const string Evaluate = "browser_evaluate";
    public const string CallCdp = "browser_cdp";
    public const string ClearData = "browser_clear_data";
    public const string Inspect = "browser_inspect";
}
