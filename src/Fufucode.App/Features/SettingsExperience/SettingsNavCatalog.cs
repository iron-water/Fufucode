using System;
using System.Collections.Generic;
using System.Linq;

namespace Fufucode.App.Features.SettingsExperience;

/// <summary>设置左侧栏的一级分类层级（D2 三级架构）。</summary>
public enum SettingsNavTier
{
    /// <summary>基础类：外观、关于、网络、键盘、模型、储存。</summary>
    Basic,

    /// <summary>大功能：浏览器、终端、文件管理、自动开发。</summary>
    Features,

    /// <summary>其他：默认收纳二级小功能配置（通知、配置、用量、辅助工具等）。</summary>
    Other,
}

/// <summary>一条左侧栏条目的定义。Icon 为 Fluent 图标键（不含 <c>Icon.</c> 前缀）。</summary>
public sealed record SettingsNavDefinition(
    string Id,
    string Title,
    SettingsNavTier Tier,
    string SearchTerms,
    string Icon = "Settings",
    string IconColor = "",
    bool HasContent = true);

/// <summary>
/// 设置左侧栏的权威目录（唯一真源）。三级分类、条目顺序与搜索词都从这里读取；
/// 升降级偏好只在其上做叠加，不改动目录本身。
/// </summary>
public static class SettingsNavCatalog
{
    /// <summary>一级分类的展示顺序与标题。</summary>
    public static readonly IReadOnlyList<SettingsNavTier> TierOrder =
    [
        SettingsNavTier.Basic,
        SettingsNavTier.Features,
        SettingsNavTier.Other,
    ];

    /// <summary>目录条目，按展示顺序排列。</summary>
    public static readonly IReadOnlyList<SettingsNavDefinition> All =
    [
        // ── 基础类 ──
        new("appearance", "外观", SettingsNavTier.Basic,
            "主题 皮肤 skin Fluent Metro Material3 Material 3 亮色 暗色 强调色 字号 密度 圆角 阅读宽度 停靠区 页面位置 显隐 入口位置 appearance theme layout dock",
            "Color", "#4CC2FF"),
        new("about", "关于", SettingsNavTier.Basic,
            "版本 数据目录 配置目录 日志 诊断 runtime about", "Info", "#8A8A8E"),
        new("network", "网络与端点", SettingsNavTier.Basic,
            "代理 自定义端点 API Key 环境变量 模型配置 URL proxy endpoint provider", "Globe", "#6CCB5F"),
        new("shortcuts", "键盘快捷键", SettingsNavTier.Basic,
            "快捷键 按键 绑定 冲突 keyboard shortcuts", "Keyboard", "#8A8A8E"),
        new("models", "模型与思考", SettingsNavTier.Basic,
            "模型 思考 等级 默认 model thinking", "Brain", "#FF9E4C"),
        new("storage", "储存", SettingsNavTier.Basic,
            "空间 占用 缓存 日志 保留 清理 storage cache logs", "Folder", "#8A8A8E"),
        new("archived", "已归档", SettingsNavTier.Basic,
            "归档 已归档 项目 会话 恢复 打开 列表 archive archived project session restore", "Archive", "#8A8A8E"),

        // ── 大功能 ──
        new("marketplace", "插件市场", SettingsNavTier.Features,
            "插件 市场 商店 安装 卸载 后端 浏览器控制 电脑操控 plugins marketplace install",
            "PuzzlePiece", "#8B5CF6", HasContent: true),
        new("browser", "浏览器", SettingsNavTier.Features,
            "Browser Use 网页 导航 链接 CDP 控制 下载 历史 缓存 权限 browser cdp web", "Globe", "#3399FF"),
        new("terminal", "终端", SettingsNavTier.Features,
            "终端 命令 shell 输出 复制 粘贴 字体 terminal shell", "Terminal", "#4CC2FF", HasContent: false),
        new("files", "文件管理", SettingsNavTier.Features,
            "文件 管理 预览 目录 打开 资源管理器 files explorer preview", "Document", "#FFC83D", HasContent: false),
        new("automation", "自动开发", SettingsNavTier.Features,
            "自动 开发 自动化 任务 线程 automation agent", "Automation", "#B48CFF", HasContent: false),

        // ── 其他（默认收纳二级小功能） ──
        new("notifications", "通知", SettingsNavTier.Other,
            "完成 失败 权限 问题 审批 提醒 失焦 notification", "Alert", "#FFC83D"),
        new("config", "配置文件", SettingsNavTier.Other,
            "JSON 导入 导出 项目覆盖 全局 ui.json config", "Code", "#B48CFF"),
        new("usage", "用量", SettingsNavTier.Other,
            "用量 token 消耗 统计 配额 usage tokens", "History", "#8A8A8E", HasContent: false),
        new("performance", "性能", SettingsNavTier.Other,
            "性能 监控 耗时 延迟 卡顿 慢 追踪 trace 诊断 perf performance trace latency", "History", "#6CCB5F"),
        new("modules", "工作台模块", SettingsNavTier.Other,
            "浏览器 启用 停用 内建 模块 辅助工具 browser module", "PuzzlePiece", "#FF99A4"),
        new("functions", "会话与消息", SettingsNavTier.Other,
            "消息 摘要 头像 名称 输入区 显示 选项 功能 function detail", "AppsList", "#4CC2FF"),
    ];

    /// <summary>默认选中的分类（第一个基础类条目）。</summary>
    public static string DefaultCategoryId => All[0].Id;

    /// <summary>分类标题（一级分类）。</summary>
    public static string TierTitle(SettingsNavTier tier) => tier switch
    {
        SettingsNavTier.Basic => "基础类",
        SettingsNavTier.Features => "大功能",
        _ => "其他",
    };

    public static SettingsNavDefinition? Find(string id) =>
        All.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));

    /// <summary>目录中的原始层级；未知 id 返回 null。</summary>
    public static SettingsNavTier? TierOf(string id) => Find(id)?.Tier;
}
