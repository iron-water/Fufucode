using System;

namespace Fufucode.App.Infrastructure;

public static class WorkbenchActionDefaults
{
    public static UiActionPlacement Placement(string id) => id switch
    {
        "toggle.left" => new(UiActionPosition.TopLeft, 0),
        "navigate.back" => new(UiActionPosition.TopLeft, 1),
        "navigate.forward" => new(UiActionPosition.TopLeft, 2),
        "session.new" => new(UiActionPosition.TopLeft, 3),
        "commands" => new(UiActionPosition.TopRight, 0),
        "toggle.bottom" => new(UiActionPosition.TopRight, 1),
        "toggle.right" => new(UiActionPosition.TopRight, 2),
        "notifications" => new(UiActionPosition.TopRight, 3),
        "settings" => new(UiActionPosition.TopRight, 4),
        "status.connection" => new(UiActionPosition.StatusLeft, 0),
        "status.shell" => new(UiActionPosition.StatusLeft, 1),
        "status.activity" => new(UiActionPosition.StatusRight, 0),
        "status.tokens" => new(UiActionPosition.StatusRight, 1),
        "status.duration" => new(UiActionPosition.StatusRight, 2),
        "status.project" => new(UiActionPosition.StatusRight, 3),
        "status.branch" => new(UiActionPosition.StatusRight, 4),
        // 会话绑定动作：默认进入顶部动态岛，可作为磁贴配置；其它皮肤自动折回左辅助菜单。
        "session.rename" or "session.pin" or "session.side" or "session.fork" or "session.schedule"
            => new(UiActionPosition.SessionIsland, id switch
            {
                "session.rename" => 0, "session.pin" => 1, "session.side" => 2,
                "session.fork" => 3, _ => 4,
            }),
        "window.session" => new(UiActionPosition.HeaderMenu),
        "help.about" or "auth.manage" or "window.new" => new(UiActionPosition.MenuRight),
        "perf" or "diagnostics.export" => new(UiActionPosition.MenuRight),
        "layout.reset" or "composer.toggle" or "summary.toggle" => new(UiActionPosition.MenuRight),
        "page.automation" or "page.local-review" => new(UiActionPosition.MenuRight),
        _ when id.StartsWith("speech.") => new(UiActionPosition.MenuRight),
        _ when id.StartsWith("help.") || id.StartsWith("view.") || id.StartsWith("edit.") => new(UiActionPosition.MenuRight),
        _ when id.StartsWith("status.") || id.StartsWith("terminal.") || id.StartsWith("browser.") => new(UiActionPosition.Hidden),
        _ => new(UiActionPosition.MenuLeft)
    };
    /// <summary>菜单二级分组：同一父名下的动作收进一个子菜单；null 表示直接平铺。</summary>
    public static string? SubMenu(string id) => id switch
    {
        "chat" or "files" or "changes" or "browser" or "terminal" or "extensions" => "工作区页面",
        "copy.id" or "copy.task" or "copy.project" or "copy.log" or "copy.deeplink" or "copy.markdown" or "copy.workdir" => "复制",
        "session.export" or "session.export.markdown" or "session.export.metadata" or "session.export.pdf" => "导出",
        "session.fork" or "session.fork.worktree" => "分叉",
        "project.open" or "project.explorer" or "project.openWith" or "library" => "项目",
        "session.search" or "session.rename" or "session.previous" or "session.next" or "session.pin" or "session.unread" or "session.archive" or "session.archives" or "session.foreground" => "会话管理",
        _ when id.StartsWith("edit.") => "编辑",
        "view.fullscreen" or "view.zoomIn" or "view.zoomOut" or "view.zoomReset" or "layout.reset" or "composer.toggle" or "summary.toggle" => "视图与布局",
        _ when id.StartsWith("speech.") => "语音朗读",
        "page.automation" or "page.local-review" or "perf" or "diagnostics.export" or "help.troubleshoot" => "诊断与系统",
        "help.shortcuts" or "help.update" or "help.about" or "auth.manage" or "window.new" or "window.session" => "帮助与全局",
        _ => null,
    };

    /// <summary>子菜单内的排序：父项（可双击执行的默认项）排第一。</summary>
    public static int SubMenuOrder(string id) => id switch
    {
        "session.export" or "session.fork" or "copy.id" or "session.search" or "edit.undo" or "view.fullscreen" or "speech.start" or "perf" or "help.about" => 0,
        _ => 10,
    };

    public static string PositionTitle(UiActionPosition position) => position switch
    {
        UiActionPosition.TopLeft => "顶栏左侧", UiActionPosition.TopRight => "顶栏右侧",
        UiActionPosition.TopCenter => "顶栏中间", UiActionPosition.SessionIsland => "会话岛（Fluent；其它皮肤折回左辅助）",
        UiActionPosition.MenuLeft => "核心功能（侧边栏）", UiActionPosition.MenuRight => "辅助功能（顶栏菜单）",
        UiActionPosition.HeaderMenu => "会话头菜单", UiActionPosition.StatusLeft => "状态栏左侧",
        UiActionPosition.StatusRight => "状态栏右侧", _ => "隐藏"
    };
    /// <summary>
    /// 动作的显式 Fluent 图标。返回 null 表示目录漏登记，调用方显示通用占位；
    /// 自测会把任何 null 视为失败，防止“新动作静默落入 AppsList”。
    /// </summary>
    public static string? ExplicitIcon(string id) => id switch
    {
        "browser.new-tab" => "Add", "browser.close-tab" => "Dismiss",
        "browser.back" => "ArrowLeft", "browser.forward" => "ArrowRight",
        "browser.reload" or "browser.zoom-reset" => "ArrowClockwise", "browser.stop" => "RecordStop",
        "browser.find" => "Search", "browser.zoom-in" => "ZoomIn", "browser.zoom-out" => "ZoomOut",
        "browser.copy-address" => "Copy", "browser.open-external" => "Open", "browser.devtools" => "Wrench",
        "browser.focus-address" => "Edit", "browser.toggle-full-view" => "FullScreenMaximize",
        "page.local-review" => "ShieldCheckmark", "page.automation" => "Automation", "library" => "Folder",
        "commands" or "session.search" => "Search", "settings" => "Settings",
        "toggle.left" => "PanelLeft", "toggle.right" => "PanelRight", "toggle.bottom" => "PanelBottom",
        "terminal" => "Terminal", "terminal.copy" => "Copy", "terminal.paste" => "ClipboardPaste",
        "terminal.find" => "Search", "terminal.bottom" => "ArrowDown", "terminal.copy-or-interrupt" => "Copy",
        "terminal.interrupt" => "RecordStop", "terminal.new" => "Add",
        "session.new" => "Add", "navigate.back" => "ArrowLeft", "navigate.forward" => "ArrowRight",
        "session.previous" => "ArrowLeft", "session.next" => "ArrowRight",
        "session.rename" => "Edit", "session.pin" => "Pin", "session.archive" => "Archive",
        "session.unread" or "chat" or "session.side" => "Chat", "session.fork" => "Branch",
        "session.export" or "session.export.markdown" => "ArrowDownload",
        "session.schedule" or "tasks" => "Calendar", "notifications" => "Alert", "browser" => "Globe",
        "files" or "project.open" => "Folder", "changes" => "Branch", "extensions" => "PuzzlePiece",
        "window.new" or "window.session" => "WindowNew", "auth.manage" => "Key",
        "project.openWith" or "project.explorer" => "Open",
        "speech.start" or "speech.settings" => "Speaker", "speech.pause" => "Pause",
        "speech.resume" => "Play", "speech.stop" => "RecordStop",
        "session.export.metadata" or "session.export.pdf" => "Document",
        "session.fork.worktree" => "Branch", "copy.id" => "Copy",
        "copy.deeplink" => "Share", "copy.markdown" => "Copy", "copy.workdir" or "copy.project" => "Folder",
        "copy.task" => "Document", "copy.log" => "History",
        "help.about" or "help.update" => "Info",
        "composer.toggle" => "Edit", "layout.reset" => "ArrowClockwise", "summary.toggle" => "Document",
        "edit.undo" => "ArrowUndo", "edit.redo" => "ArrowRedo", "edit.cut" => "Cut", "edit.copy" => "Copy",
        "edit.paste" => "ClipboardPaste", "edit.delete" => "Dismiss", "edit.selectAll" => "Document",
        "view.zoomIn" => "ZoomIn", "view.zoomOut" => "ZoomOut", "view.zoomReset" => "ArrowClockwise",
        "view.fullscreen" => "FullScreenMaximize",
        "session.archives" => "Archive", "session.foreground" => "Open",
        "help.shortcuts" => "Keyboard", "perf" => "Speedometer", "help.troubleshoot" => "Wrench",
        "diagnostics.export" => "ArrowDownload",
        _ when id.StartsWith("status.", StringComparison.Ordinal) => "Info",
        _ => null,
    };

    /// <summary>界面用图标：显式映射优先，未知动作回退通用图标。</summary>
    public static string Icon(string id) => ExplicitIcon(id) ?? "AppsList";
}
