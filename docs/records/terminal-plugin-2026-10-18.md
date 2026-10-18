# 交互终端插件化与体验增强交付记录（2026-10-18）

工作树：`C:/Projects/Fucode/Fufucode/.claude/worktrees/terminal-p0`
分支：`worktree-terminal-p0`
用户指令：撤销 2026-10-16 “终端保留内置”的例外，终端同样拆成插件；随后要求把所有已知问题和可做的增强写好。

## 1. 拆分结果

- 新增独立工程 `src/Fufucode.Plugins.Terminal`，插件 ID `workspace.terminal`，manifest 版本 `2.0.0`。
- 终端视图、视图模型、ConPTY 会话、VT 缓冲区、选择/查找/历史/IME 模块全部迁出 `Fufucode.App`。
- 宿主侧新增：
  - `Fufucode.Core.Plugins.PluginPackageManager.PageContributions` 与 `PageContributionsChanged`：插件激活时登记页面贡献，停用时移除。
  - `Fufucode.App.Features.Plugins.IPluginWorkspacePage`：宿主与页面插件之间的最小契约（视图、可见性、项目切换、快捷键匹配、复制/粘贴/查找/重启/关闭）。
  - `PluginWorkspacePageHost`：只在插件已安装且已启用时创建页面并注册进停靠区；未安装/未启用/构造失败时不显示空壳，动作返回真实原因（如“未安装或未启用交互终端插件”）。
  - `WorkbenchPageRegistry.Unregister` 与布局清理：插件停用后移除死页签。
- 终端动作、快捷键作用域、状态栏与系统状态入口改为走页面契约，不再引用终端类型。
- 打包：`package-native.ps1` 将 `workspace.terminal` 发布到 `out/plugins-dist`，并在默认 App 包校验中禁止 `Fufucode.Plugins.Terminal.dll`。
- 自测模式：`SelfTestTerminalPlugin` 仅在 `--selftest` 下从开发构建或 `out/plugins-dist` 安装真实载荷到隔离数据根，保证界面自测覆盖；生产路径不安装、不打包。

## 2. 已实现的问题修复与增强

### VT / 会话
- 备用屏 `?47/?1047/?1049`：全屏程序独立屏幕，滚动不污染主屏回滚，退出恢复主屏。
- DSR/DA 应答：`CSI 5n/6n`、`CSI c`、`CSI >c`，视图模型把应答写回 shell。
- 滚动区域 `DECSTBM`、原点模式、`IL/DL/ICH/DCH/ECH/REP`、xterm 延迟换行。
- SGR：下划线、斜体、反显、暗色、隐藏、删除线；粗体与 256/真彩色。
- OSC 0/1/2 标题、OSC 4 调色板覆盖、OSC 8 超链接；BEL 事件。
- `DECSCUSR` 光标形状、`?2004` 括号粘贴、鼠标跟踪模式暴露到快照。
- 组合字符并入前一单元格；覆盖宽字符续格时清掉半个字。
- 可配置回滚上限；收缩时保留文本并处理宽字符边界。

### 交互 / 界面
- 双击选词、三击选整行、Ctrl+Shift+A 全选、右键“全选/打开链接”。
- Ctrl+Click 打开 OSC 8 链接；悬停高亮。
- 拖拽超出视口按行自动滚动。
- 鼠标跟踪（1000/1002/1003/1006）按下/释放/滚轮转发；Shift 保留文本选择。
- 页签右键重命名/重启；工具栏重启按钮；shell 退出后可重启。
- 终端设置对话框：shell 配置、命令覆盖、字号、回滚行数、响铃方式、光标闪烁、链接、关闭确认；持久化到插件自己的 `terminal-settings.json`。
- shell 配置：cmd、Windows PowerShell、PowerShell 7、Git Bash；缺失时明确报告，不回退到其它 shell。
- 直接粘贴到 shell（右键菜单或 Ctrl+Shift+V）；程序启用括号粘贴时自动包裹，未启用且含换行时先确认。
- 字号驱动单元格度量；Ctrl +/- 缩放；视觉响铃闪烁或蜂鸣。
- 文本绘制按同样式相邻单元格合并，`FormattedText` 按文本/颜色/样式/字号缓存。
- 输出面提供 AutomationPeer（有选区时名称为选中文本）与帮助文本。

## 3. 验证

实际执行：

- `dotnet build Fufucode.sln`：36 工程，0 错误，0 警告。
- `tests/Fufucode.App.Terminal.Tests`：104 项断言全部通过，覆盖 VT P0/P1、备用屏、DSR/DA、滚动区域、SGR、OSC、模式、组合字符、调色板、选择、设置、插件页面契约，以及真实 `MainWindow` 有/无插件两条路径。
- `dotnet run --project src/Fufucode.App -- --selftest --r2-only`：0 失败。
- `--floating-workbench-only`：0 失败。
- 真实 ConPTY 冒烟（在测试工程/自测入口中）：光标报告应答可见；主屏提示符保留。
- 全量 App `--selftest`：12 项失败，均不属于终端：W5-R3 设置分类 2 项（另有 2 项连带异常）、Tabs 空区域 2 项、C5 原生目标输入/焦点 4 项、R05 设置行 2 项。终端相关断言 0 失败。

未通过或未验证：

- `--tabs-only` 在本工作树仍有 2 项失败（空区域加页入口、`FilesContent already has a visual parent`）。在禁用插件页面初始化后同样复现，属本工作树基线问题，不是终端插件引入；未修复。
- `--skins-integration-only` 在切换皮肤后出现 `Grid.GetMeasureSizeForRange` 越界异常；禁用插件页面初始化后同样复现，属基线问题；未修复。
- `--r3-only` 的设置分类断言在本工作树失败；与终端无关，未修复。
- 未做原生人工验收：真实窗口中的鼠标跟踪、拖拽自动滚动、链接打开、响铃、设置对话框、PowerShell/Git Bash 交互。
- 未验证：真实 IME 候选框跟随终端输出光标；终端目前是 Windows ConPTY。

## 4. 未实现（如实列出）

- 分屏（split panes）与页签拖拽排序；当前只有重命名/重启。
- 命令历史跨会话持久化（仍按页签驻留内存）。
- 正则/全词查找；当前是大小写可选的子串查找。
- 鼠标拖动运动事件（1002/1003 的 motion 上报）与 1005/1015 编码；当前只发按下/释放/滚轮。
- 图片协议（Sixel/Kitty/iTerm2）、Unicode 模糊宽度配置。
- 回滚不跨应用重启持久化。
- 终端插件仍只支持 Windows ConPTY，非 Windows 平台报告不可用。

## 5. 插件生命周期说明

- 安装/启用：从 `out/plugins-dist/workspace.terminal-2.0.0.zip` 或 `distribution/workspace.terminal` 安装后，宿主在插件管理器激活时动态注册页面；停用/卸载时页面移除，布局自动清理。
- 终端设置保存在插件数据根（`<dataRoot>/terminal-settings.json`），卸载插件时按插件的“保留数据”策略保留。
- 默认包不包含终端插件 DLL；未安装时终端动作显示“未安装或未启用交互终端插件”。
