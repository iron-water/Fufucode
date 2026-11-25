namespace Fufucode.Fui;

internal enum CommandKind { Local, Session, Files }
internal sealed record FuiCommand(string Id, string Label, string Group, CommandKind Kind = CommandKind.Local,
    string Argument = "", bool Picker = false, bool NeedsText = false);

// Help, the searchable palette, slash completion and execution use the same registrations.
internal static class FuiCommands
{
    public static readonly FuiCommand[] All =
    [
        new("commands", "命令面板", "导航"), new("help", "按键与命令", "导航"),
        new("settings", "设置中心", "设置", Argument: "[关键词]"),
        new("theme", "主题", "设置"), new("terminal", "终端能力", "设置"),
        new("doctor", "诊断", "设置"), new("keys", "按键绑定", "设置"),
        new("new", "新建会话", "会话", CommandKind.Session),
        new("resume", "恢复项目会话", "会话", CommandKind.Session, "[ID]", Picker: true),
        new("tree", "会话分支树", "会话", CommandKind.Session, "[ID]", Picker: true),
        new("fork", "从历史消息分叉", "会话", CommandKind.Session, "[节点]", Picker: true),
        new("rename", "重命名当前会话", "会话", CommandKind.Session, "<名称>", NeedsText: true),
        new("export", "导出 Markdown", "会话", CommandKind.Session, "[文件]"), new("session", "当前会话", "会话"),
        new("info", "会话详情", "会话"),
        new("model", "切换模型", "模型", CommandKind.Session, "[模型 ID]", Picker: true),
        new("thinking", "思考等级", "模型", CommandKind.Session, "[等级]", Picker: true),
        new("usage", "查看用量", "模型", CommandKind.Session),
        new("skills", "选择本会话技能", "模型", CommandKind.Session, Picker: true),
        new("capabilities", "浏览已注册能力", "模型", CommandKind.Session, Picker: true),
        new("mode", "执行模式", "回合", CommandKind.Session, "[模式]", Picker: true),
        new("steer", "向运行中的回合插话", "回合", CommandKind.Session, "<文本>", NeedsText: true),
        new("queue", "查看或追加跟进消息", "回合", CommandKind.Session, "[文本]"), new("stop", "停止回合", "回合"),
        new("retry", "重发上一条用户消息", "回合"),
        new("agents", "子代理活动", "回合", Picker: true),
        new("plan", "计划面板", "工作流"), new("todo", "待办清单", "工作流"),
        new("stats", "会话统计", "工作流"), new("changes", "Git 变更浏览", "工作流"),
        new("review", "审查工作区变更", "工作流"),
        new("attach", "附加项目文件", "项目", CommandKind.Files, "[路径]", Picker: true),
        new("paste-image", "粘贴剪贴板图片", "项目", CommandKind.Files),
        new("paste", "粘贴剪贴板图片（/paste-image 别名）", "项目", CommandKind.Files),
        new("detach", "移除草稿附件", "项目", CommandKind.Files, "[路径]", Picker: true),
        new("context", "查看待发送文件", "项目"),
        new("preview", "预览项目文件", "项目", CommandKind.Files, "[路径]", Picker: true),
        new("diff", "查看文件 Git 差异", "项目", CommandKind.Files, "[路径]", Picker: true),
        new("find", "搜索并跳转到消息", "阅读", Argument: "[关键词]", Picker: true),
        new("links", "链接目标", "阅读", Argument: "[关键词]", Picker: true),
        new("outline", "会话大纲", "阅读"), new("copy", "复制正文", "阅读"),
        new("transcript", "对话浏览器", "阅读"),
        new("history", "找回本次输入", "编辑", Argument: "[关键词]", Picker: true),
        new("tools", "展开或折叠工具输出", "阅读"), new("thoughts", "隐藏或显示思考", "阅读"),
        new("quit", "退出 FUI", "导航")
    ];
    private static readonly Dictionary<string, FuiCommand> Index = All.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
    public static FuiCommand? Get(string id) => Index.GetValueOrDefault(id);
    public static FuiChoice Choice(FuiCommand c) => new(c.Id, "/" + c.Id, c.Label + " · " + c.Group);
    public static string Help => HelpFor(FuiSettings.Default);
    public static string HelpFor(FuiSettings settings) =>
        TuiController.KeyHelp + "\n\n快捷键\n" + FuiKeyBindingCatalog.Help(settings) +
        "\n\n设置\n" + FuiSettingCatalog.Help(settings) + "\n\n命令\n" +
        string.Join("\n", All.GroupBy(c => c.Group)
            .Select(g => g.Key + "  " + string.Join("  ", g.Select(c =>
                "/" + c.Id + (c.Argument == "" ? "" : " " + c.Argument)))));
}
