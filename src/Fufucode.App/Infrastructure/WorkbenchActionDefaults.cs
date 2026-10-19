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
        "status.duration" or "status.branch" or "status.project" => new(UiActionPosition.Hidden),
        "session.rename" or "session.pin" or "session.side" or "session.fork" or "session.search" or "session.share" or "session.archive" or "session.schedule" => new(UiActionPosition.SessionIsland, 0),
        "window.session" => new(UiActionPosition.HeaderMenu),
        "help.about" or "auth.manage" or "window.new" => new(UiActionPosition.MenuRight),
        "speech.stop" or "speech.start" or "usage" or "trace" => new(UiActionPosition.MenuRight),
        _ when id.StartsWith("help.") || id.StartsWith("view.") || id.StartsWith("edit.") => new(UiActionPosition.MenuRight),
        _ when id.StartsWith("status.") => new(UiActionPosition.Hidden),
        _ => new(UiActionPosition.MenuLeft)
    };
    /// <summary>菜单二级分组：同一父名下的动作收进一个子菜单；null 表示直接平铺。</summary>
    public static string? SubMenu(string id) => id switch
    {
        _ when id.StartsWith("browser.") => "浏览器操作",
        "chat" or "files" or "changes" or "browser" or "terminal" or "extensions" => "工作区页面",
        "copy.session" or "copy.id" or "copy.task" or "copy.project" or "copy.log" or "copy.deeplink" or "copy.markdown" or "copy.workdir" => "复制",
        "session.export" or "session.export.markdown" or "session.export.metadata" or "session.export.pdf" => "导出",
        "session.fork" or "session.fork.worktree" => "分叉",
        "project.open" or "project.explorer" or "project.openWith" => "项目",
        _ => null,
    };

    /// <summary>子菜单内的排序：父项（可双击执行的默认项）排第一。</summary>
    public static int SubMenuOrder(string id) => id switch
    {
        "session.export" or "session.fork" or "copy.id" => 0,
        _ => 10,
    };

    public static string PositionTitle(UiActionPosition position) => position switch
    {
        UiActionPosition.TopLeft => "顶栏左侧", UiActionPosition.TopRight => "顶栏右侧",
        UiActionPosition.TopCenter => "顶栏中间", UiActionPosition.SessionIsland => "会话岛（顶栏中央）",
        UiActionPosition.MenuLeft => "核心功能（侧边栏）", UiActionPosition.MenuRight => "辅助功能（顶栏菜单）",
        UiActionPosition.HeaderMenu => "会话头菜单", UiActionPosition.StatusLeft => "状态栏左侧",
        UiActionPosition.StatusRight => "状态栏右侧", _ => "隐藏"
    };
    public static string Icon(string id) => id switch
    {
        "browser.new-tab" => "Add", "browser.close-tab" => "Dismiss", "browser.back" => "ArrowLeft", "browser.forward" => "ArrowRight",
        "browser.reload" or "browser.zoom-reset" => "ArrowClockwise", "browser.stop" => "RecordStop", "browser.find" => "Search",
        "browser.zoom-in" => "ZoomIn", "browser.zoom-out" => "ZoomOut", "browser.copy-address" => "Copy",
        "browser.open-external" => "Open", "browser.devtools" => "Wrench",
        "page.local-review" => "ShieldCheckmark",
        "commands" or "session.search" => "Search", "settings" => "Settings",
                "toggle.left" => "PanelLeft", "toggle.right" => "PanelRight", "toggle.bottom" => "PanelBottom", "terminal" => "Terminal",
        "session.new" => "Add", "navigate.back" => "ArrowLeft", "navigate.forward" => "ArrowRight",
        "session.rename" => "Edit", "session.pin" => "Pin", "session.archive" => "Archive",
        "session.unread" or "chat" or "session.side" => "Chat", "session.fork" => "Branch", "session.export" => "ArrowDownload", "session.share" => "Share",
        "session.schedule" or "tasks" => "Calendar", "notifications" => "Alert", "browser" => "Globe", "files" or "project.open" => "Folder",
        "extensions" => "PuzzlePiece", "window.new" or "window.session" => "WindowNew", "usage" => "Info",
        "auth.manage" => "Key", "project.openWith" => "Open",
        "speech.start" or "speech.settings" => "Speaker", "speech.pause" => "Pause", "speech.resume" => "Play",
        "session.export.metadata" => "Document",
        "session.fork.worktree" => "Branch", "copy.deeplink" => "Share", "copy.markdown" => "Copy", "copy.workdir" => "Folder",
        "help.about" or "help.update" or "help.performance" => "Info",
        "composer.toggle" => "Edit", "layout.reset" => "ArrowClockwise", "edit.undo" => "ArrowUndo", "edit.redo" => "ArrowRedo",
        "edit.cut" => "Cut", "edit.copy" => "Copy", "edit.paste" => "ClipboardPaste", "edit.delete" => "Dismiss", "edit.selectAll" => "Document",
        "view.zoomIn" => "ZoomIn", "view.zoomOut" => "ZoomOut", "view.zoomReset" => "ArrowClockwise", "view.fullscreen" => "FullScreenMaximize",
        "speech.stop" => "RecordStop", "session.archives" => "Archive", "session.foreground" => "Open", "project.explorer" => "Folder",
        "help.shortcuts" => "Keyboard",
        "trace" => "History", "help.tasks" => "AppsList", "help.troubleshoot" => "Wrench", "help.status" => "Info",
        _ when id.StartsWith("status.") => "Info",
        _ when id.StartsWith("copy.") => "Copy", _ => "AppsList"
    };
}
