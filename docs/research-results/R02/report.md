# R02 研究报告：Windows 多窗口与多进程

- 任务书：`docs/research-tasks/R02-windows.md`
- 基线：本仓 `main@4d7c2a26cbdab744924dadb466990783ef2f7725`；未提交原型 SHA-256 见 `claim.md`
- 日期：2026-10-05。状态：研究稿，不构成架构决定；实施由主 agent 决定
- 本次**未运行任何程序**（未构建、未启动 Pi、未做 UI 实验）：全文无"实测通过"结论；所有行为结论均标注证据级别

## 1. 结论摘要

1. **本仓栈完全支持真实多顶级窗口**。Avalonia 12.1.3 的 Window/生命周期 API（`ShutdownMode`、`lifetime.Windows`、`Show()`/`Show(owner)`、`ShowInTaskbar`、`WindowState`、`Screen`）与 Win32 后端（`WS_EX_APPWINDOW`/`WS_EX_TOOLWINDOW`、`MonitorFromWindow`）均齐备（§4、§5）。"Windows harness 无此能力"不成立。
2. **会话所有权已经是跨进程安全的**：Core 的 `SessionLease`（FileShare.None 文件锁）+ 每 `PersistentSession` 一个 Pi 子进程 + 元数据 SQLite。多窗口方案 A（同进程多顶级窗口）基本是 UI 层工作，不需要新协议（§6、§8）。
3. **推荐方案 A**：同进程多个无主（unowned）顶级窗口，每窗口一个会话控制器（RealSessionHost），每会话仍一个 Pi 子进程；`ShutdownMode.OnLastWindowClose`（现默认）天然支持"关单窗不死应用、关满窗才退出"。按成本优先交付，独立进程（方案 B）留到有崩溃隔离/资源需求证据后再决定（§6）。
4. 关键坑：**owned window（`Show(owner)`）没有任务栏按钮且随 owner 最小化而隐藏**——会话窗口必须 `Show()` 无主显示，`Show(owner)` 只留给审批弹窗（现有 ExtensionUiDialogWindow 正是此用法）；任务栏分组默认按进程（Avalonia 12.1.3 不设置 AppUserModelID，二进制核对 0 处引用），两窗会归入同一任务栏组，属可接受默认，逐窗分组需窗口级 AUMID（P/Invoke，非 Avalonia 内置）。
5. "复制只读视图"在**同进程内是低成本能力**（第二个窗口订阅同一 `PersistentSession` 的 `SnapshotChanged`，禁用输入/停止/审批），跨进程只读视图未证实可行（Pi 无只读会话保证），列为开放问题。
6. fork 已有现成 RPC 支持（`PersistentSessionManager.ForkAsync`，仅 Idle 可用），"fork 到新窗口"是最干净的第三入口。

## 2. 证据基线与样本边界

| 类别 | 对象 | 版本/位置 |
|---|---|---|
| 本仓源码事实 | `src/`、`src/Fufucode.Core` 等 | commit `4d7c2a2` |
| 框架源码事实 | AvaloniaUI/Avalonia | tag `12.1.3`，tag 对象 `8eeda4f6f546165b3f72e63c9f42247abb306905`（gh 认证拉取核对） |
| 框架二进制核对 | `~/.nuget/packages/avalonia[.win32]/12.1.3/lib/net8.0/*.dll` | 元数据字符串检查（ShowInTaskbar/WS_EX_APPWINDOW/TaskBarList 等） |
| Windows 行为 | Microsoft Learn 四页 | §4 引用（2025-07 版本页面） |
| 运行环境 | Windows x64，build `10.0.26300`；Node 24.15.0、Pi 0.87.1（runtime-baseline.md） | 环境事实 |

**样本边界声明**：Windows harness 能力核实样本 = Avalonia 12.1.3（源码 tag + nuget 程序集）+ 本仓 4d7c2a2 + Microsoft Learn 官方文档四页。结论限定于此样本，不穷举其他 GUI 框架或其他 harness；"没有其他 harness 支持"类判断一律不在此报告做出。

## 3. 本仓现状（源码事实，commit 4d7c2a2）

### 3.1 版本与生命周期

- Avalonia **12.1.3**，`net10.0`（`src/Fufucode.App/Fufucode.App.csproj:37-42`，另含 Headless 包）。
- 经典桌面生命周期：`Program.cs:40` `StartWithClassicDesktopLifetime`；`App.axaml.cs:55` `desktop.MainWindow = new MainWindow(_options)`——**当前单窗口**，无 ShutdownMode 设置（默认值含义见 §5.1）。
- 产品启动参数（`AppOptions.Parse`）：`--project`、`--session`、`--mock`、`--pi-node/--pi-cli`。`--session <id>` 已支持"启动即恢复指定会话"，可直接被"新窗口"复用。

### 3.2 会话协调器边界（Pi 所有权）

- **每会话一个 Pi 子进程**：`PersistentSessionManager.OpenAsync`（`PersistentSessionManager.cs:210-302`）为每个会话新建 `ChatSessionCoordinator`（其 `PiRpcClient` 以 `runtime/node/node.exe + cli.js --mode rpc` 启动子进程），一个 `PersistentSession` = 协调器 + 历史 + 租约。
- **跨进程独占锁已存在**：`SessionLease.Acquire`（`AppDataLayout.cs:49-84`）对 `sessions/<projectId>/.locks/<sha256(sessionId)>.lock` 打开 `FileShare.None` 文件流；占用时 `IOException` → `InvalidOperationException("This session is already owned by another process.")`。**同窗口进程内**第二处 `ResumeAsync` 同会话同样被拒。
- **宿主单活动会话**：`RealSessionHost`（`RealSessionHost.cs`）持一个 manager + 一个当前会话；切换/新建"先开后放"；`RestartCurrentAsync` 因租约被旧进程持有而必须"先放后开"。注释明确"进程与会话独占由 Core 的 SessionLease 保证"。
- **fork 复用进程**：`ForkAsync`（`PersistentSessionManager.cs:147-202`）在同一 Pi 进程上 fork，子租约先于父租约释放获取（锁交接），仅 Idle 可 fork。
- **进程清理**：`PiRpcClient` 释放时 `Kill(entireProcessTree: true)`（`PiRpcClient.cs:313`）。
- **事件广播可多订阅**：`ChatSessionCoordinator.SnapshotChanged` 是普通 .NET 事件（`ChatSessionCoordinator.cs:34`），同进程多个视图可同时订阅；`SessionSnapshot` 携带活动状态、队列、待审批请求。
- **审批弹窗已按 owner 窗口化**：`ExtensionUiDialogHost.cs:84` `window.Show(_owner)`（Avalonia owned window 用法，见 §5.2 的行为后果）。
- **元数据 SQLite**：`DesktopMetadataStore.cs:34-43` `Pooling=false`、`Default Timeout=10` 秒、`foreign_keys=ON`；未设 WAL（默认回滚日志）。同进程多连接与跨进程并发写均依赖 SQLite 文件锁 + 10s busy timeout，**未实测**。
- **UI 可复用性**：`MainWindow.AttachSession`（`MainWindow.axaml.cs:99` 起）为每窗口构建 `MainViewModel`；`ChatPage` 继承窗口 DataContext——会话 UI 与 MainWindow 的耦合点集中在 Attach/工作台 partial（Chrome/Workspace/Global/SettingsNavigation），ChatPage 与 MainViewModel 本身是窗口无关的。

### 3.3 关闭路径

`MainWindow.OnClosing`（`MainWindow.axaml.cs:967`）：取消默认关闭 → 设置确认 → 保存草稿 → 释放终端/浏览器/弹窗宿主 → `_host.DisposeAsync()`（关 Pi）→ 再 `Close()`。逻辑绑在"唯一主窗口"假设上，多窗口时此模式可逐窗复制，但需确认 `App.SettingsStore` 静态单例（`App.axaml.cs:13-20`）在多窗口下仍成立（同进程同 AppOptions，成立）。

## 4. Windows 行为（官方文档事实 + Avalonia 实现事实）

### 4.1 任务栏按钮与 Alt+Tab（Microsoft Learn 原文）

- 任务栏按钮判据（[The Taskbar](https://learn.microsoft.com/en-us/windows/win32/shell/taskbar)）："The Shell creates a button on the taskbar whenever an application creates a window that isn't owned. To ensure that the window button is placed on the taskbar, create an unowned window with the **WS_EX_APPWINDOW** extended style. To prevent the window button from being placed on the taskbar, create the unowned window with the **WS_EX_TOOLWINDOW** extended style. As an alternative, you can create a hidden window and make this hidden window the owner of your visible window."
- WS_EX_TOOLWINDOW 明确排除 Alt+Tab（[Extended Window Styles](https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles)）："A tool window does not appear in the taskbar or in the dialog that appears when the user presses ALT+TAB."；WS_EX_APPWINDOW "Forces a top-level window onto the taskbar when the window is visible."
- owned window 三条约束（[Window Features](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features)）：始终在 owner 之上；owner 销毁时被销毁；**owner 最小化时被隐藏**。
- **推论（文档→我们的设计）**：两个无主顶级窗口 ⇒ 各自出现在任务栏与 Alt+Tab。owned window（`Show(owner)`）⇒ 无任务栏按钮且随 owner 最小化隐藏 ⇒ 会话窗口不可用 owned 形式。

### 4.2 任务栏分组（AppUserModelID）

[AppUserModelIDs](https://learn.microsoft.com/en-us/windows/win32/shell/appids)：未显式声明 AUMID 时系统用启发式分配 internal AUMID（应用无法读取它）；要按窗口拆分分组用 `SHGetPropertyStoreForWindow` 设窗口级 `System.AppUserModel.ID`，窗口级覆盖进程级；要整体分组用 `SetCurrentProcessExplicitAppUserModelID`。**同一进程的多个无主窗口默认归入同一任务栏组**（启发式按进程归组）——此条为文档+启发式推断组合，本机未实测。

### 4.3 Avalonia 12.1.3 实现（tag `8eeda4f` 源码 + 本地程序集核对）

| 能力 | 事实 | 位置（12.1.3 tag） |
|---|---|---|
| 多窗口注册 | `IClassicDesktopStyleApplicationLifetime.Windows`（`IReadOnlyList<Window>`），框架自动跟踪 | `src/Avalonia.Controls/ApplicationLifetimes/ClassicDesktopStyleApplicationLifetime.cs:51` |
| 关闭→退出语义 | 窗口关闭时：`OnLastWindowClose && _windows.Count == 0` 或 `OnMainWindowClose && window == MainWindow` 才 Shutdown | 同上 `:61-63` |
| ShutdownMode | 枚举首成员 `OnLastWindowClose`（未显式设置时的默认） | `src/Avalonia.Controls/ShutdownMode.cs` |
| OS 关机 | 向所有非 owned 窗口发关闭、可被取消 | 同上 `:198-213` |
| 无主显示 | `Show()`；**owned 显示 `Show(Window owner)`**（要求 owner 可见，closed owner 抛异常） | `src/Avalonia.Controls/Window.cs:939` |
| owner 关闭级联 | `WindowClosingBehavior.OwnerAndChildWindows`/`OwnerWindowOnly` | 同上 `:80,86,645,671` |
| ShowInTaskbar | 默认 `true`；false 时 Win32 后端把窗口挂到隐藏父窗口（无法显示任务栏图标） | 同上 `:158-159`；`src/Windows/Avalonia.Win32/WindowImpl.cs:1425-1464` |
| Win32 样式 | `ShowInTaskbar=true → WS_EX_APPWINDOW`；false → 清除并挂 OffscreenParentWindow | `WindowImpl.cs:1421-1464` |
| 最小化/恢复 | `WindowState`（Minimized/Maximized/Normal…），`ShowWindow(state, activate)`；激活走 `SetForegroundWindow` | `WindowImpl.cs:1266-1315,660` |
| 跨显示器 | `MonitorFromWindow(MONITOR_DEFAULTTONEAREST)`（DPI 按最近显示器）；`Screen.AllScreens` 可查 | `WindowImpl.cs:543,1028`；`Avalonia.Controls/Screens.cs` |
| 任务栏集成 | `TaskBarList.SetOverlayIcon`/`SetProgressValue`（覆盖图标/进度）；**未发现 AppUserModelID 相关引用**（二进制字符串 0 处，源码未含） | `WindowImpl.cs:877`；本地 `Avalonia.Win32.dll` 字符串核对 |

### 4.4 焦点与激活

`SetForegroundWindow` 受系统限制（前台锁超时、菜单激活中等，Window Features 原文）。Avalonia 在显示/激活路径调用它（`WindowImpl.cs:660,1315`）。多窗口下"后台窗口来消息时闪任务栏"需用 `FlashWindow`（文档建议），Avalonia 未封装——本仓尚未用到，列为可选增量，**未实测**。

## 5. 三种方案对比（任务书第 1 问）

前提事实：Pi RPC 模式一个进程绑定一个会话（runtime-baseline 实测 `get_state.sessionId` 单数；本仓每会话起一个子进程）。因此"共享 Pi 进程"不在可行集合内；三种方案的差异在**桌面应用进程与窗口的组织方式**，Pi 子进程都保持每会话一个。

| 维度 | A. 同进程多顶级窗口 | B. 每窗口独立 app 进程 | C. 共享后台协调器（进程内集中会话服务） |
|---|---|---|---|
| 结构 | 每窗口一个会话控制器（RealSessionHost），窗口是平级无主窗口 | 每窗口一个 Fufucode 进程，各自 manager/host；单实例逻辑改为"转发新窗口" | App 级 SessionService 持全部会话，窗口仅绑定 sessionId 的视图 |
| 会话所有权 | SessionLease 现成生效（跨窗口=同进程锁） | SessionLease 跨进程生效（现状即为此设计） | 同 A；生命周期集中在服务层 |
| Pi 子进程 | 每会话 1 个 | 每会话 1 个（不省） | 每会话 1 个（不省） |
| 资源 | 1 份 .NET UI 进程；N 个 Node 子进程 | N 份 .NET UI 进程 + N 个 Node | 同 A |
| 崩溃隔离 | UI 线程崩溃全窗口齐崩；单会话 Pi 崩溃只影响本窗（Faulted 状态已按会话隔离） | 窗口崩溃不影响其他窗口（最强） | 同 A；服务崩溃全窗口齐崩 |
| 只读多视图 | 天然（进程内事件订阅，§7） | 不可（跨进程无只读通道，见开放问题） | 天然 |
| 任务栏/Alt+Tab | 同组（默认按进程分组），组内每窗一按钮/一项 | 同 AUMID 显式归组可完全一致（需每进程设同一 AUMID） | 同 A |
| 关闭语义 | OnLastWindowClose 默认即可 | 需单实例协议 + 末窗退出协调 | 同 A（或 OnExplicitShutdown+托盘，超范围） |
| 草稿/设置一致性 | 进程内共享，天然一致 | 各进程共享 SQLite（10s busy timeout，未实测争用） | 进程内共享 |
| 改造成本 | **最低**：新 Window 类 + 复用 AttachSession 路径；MainWindow partial 无需大改 | 高：进程间"新窗口"路由、参数转发、AUMID、退出协调、诊断聚合 | 中：重构 host 归属，动 MainWindow 装配 |
| 主要风险 | MainWindow partial 耦合（工作台/终端/浏览器资源按窗复制）；单 UI 线程 | 进程间 IPC 面扩大；重复初始化成本；拖拽/会话列表跨进程 | 集中服务的生命周期复杂度（零窗口驻留问题） |

被否决形态：**dock 弹层/浮动面板**（Avalonia 内嵌浮窗）——不产生独立任务栏/Alt+Tab 项，不满足任务书"不应只是 dock 弹层"；仅可作为窗内辅助 UI，不算多窗口。

**推荐：A，分两步**。第一步只做"新窗口/fork 到新窗口/只读视图"（成本最低、复用现有一切）；第二步按需评估 B（触发条件见 §13 实施影响）。反对 A 的理由（如实陈述）：UI 线程单点（任一窗口渲染卡顿全体感知）、窗口数量大时内存随 UI 内容增长、MainWindow 现有 partial 拆分有回归风险；这些都不构成 B 的当下必要性，因为核心诉求（Alt+Tab 快切、同时观察多个 agent、真窗口语义）A 全部满足。

## 6. 窗口/会话/Pi 所有权图

现状（单窗口）：

```text
Fufucode.App 进程 (1 个 UI 线程)
  MainWindow (desktop.MainWindow, ShutdownMode=OnLastWindowClose 默认)
    MainViewModel ── ChatPage
    RealSessionHost
      PersistentSessionManager (元数据 SQLite / 设置 / 凭据)
      PersistentSession = ChatSessionCoordinator ──stdin/stdout JSONL──> node.exe (Pi --mode rpc)  [每会话 1 个]
                         + SessionLease (sessions/<proj>/.locks/<sha>.lock, FileShare.None)          [跨进程互斥]
                         + SessionHistory / DiagnosticLog
    ExtensionUiDialogHost ── ExtensionUiDialogWindow.Show(owner=MainWindow)   [owned, 无任务栏按钮]
    终端/浏览器页 (ConPTY 子进程 / WebView2)                                    [窗口附属资源]
```

方案 A 目标态（两窗示例）：

```text
Fufucode.App 进程
  MainWindow W1 (项目 P1, 会话 S1)                SessionWindow W2 (无主, 会话 S2)
    RealSessionHost#1                              RealSessionHost#2
      PersistentSession S1 ──> node.exe #1           PersistentSession S2 ──> node.exe #2
      SessionLease(S1) 持有                           SessionLease(S2) 持有
    ExtensionUiDialogHost(owner=W1)                 ExtensionUiDialogHost(owner=W2)
  只读视图窗 W3 = S1 的第二个视图（订阅 S1.SnapshotChanged，无租约、无输入）   [可选形态]
  ─────────────────────────────────────────────────────────────────────────
  写权不变量：会话 S 的唯一写者 = 持有 SessionLease(S) 的 PersistentSession；
  每个窗口至多持有一个可写会话；同一 S 在全系统（含其他 Fufucode 进程）至多一个租约。
```

## 7. 一窗一会话 vs 一会话多视图；冲突防护（任务书第 3 问）

- **区分标准 = 是否持写租约**。可写窗口 = 持 `SessionLease` 的窗口（一窗一活动会话，现 `RealSessionHost` 语义）；只读视图 = 同进程内订阅同一 `PersistentSession` 的 `SnapshotChanged`/`ProtocolEvent`，不持租约、不发送任何命令。
- **同一 Pi 会话只能由谁写入**：产品不变量（本仓已实现）= 持租约的那一个 `PersistentSession`（跨 app 进程同样成立）。Pi 自身对会话文件是否加锁**未证实**（无文档，本仓未实验）；因此跨进程双开同会话文件属禁止形态，靠租约拦截。
- **草稿**：`desktop.db` 按 sessionId 存（`RealSessionHost.SaveDraft/LoadDraft`）。只读视图禁止写草稿；"移到新窗口"随会话迁移；可写窗口切换会话时保存当前草稿（现 OnClosing/切换路径已做）。
- **排队与停止**：队列/`clear_queue`+`abort`/steer/follow_up 都是协调器状态与命令（`ChatSessionCoordinator`），只属于持租约窗口；只读视图的"停止"按钮必须隐藏或禁用（UI 约束 + 建议在视图层不暴露 `IChatSession` 写方法）。
- **授权弹窗**：审批请求经协调器 `ExtensionUiRequestReceived` 到 `ExtensionUiDialogHost`，弹窗 owner 应绑定**持租约窗口**。多窗口时一个窗口的审批弹窗不应模态阻塞其他窗口（`Show(owner)` 是非模态 owned 窗口，满足）。风险：`ExtensionUiDialogHost._owner` 固定单窗，"移到新窗口"时需重挂 owner。
- **投影一致性**：同一会话的写窗与只读视图共享同一协调器快照，不存在双渲染分叉；跨进程则无此保证（开放问题）。

## 8. 四个动作的语义（任务书第 5 问）

| 动作 | 语义 | 复用点 | 备注 |
|---|---|---|---|
| **新窗口** | 创建无主顶级窗口，新会话（`CreateAsync`）或从会话列表选既有会话（`ResumeAsync`；被占则给"会话已由另一窗口打开"并引导只读视图） | `AppOptions` 已支持 `--project/--session`；`SessionLease` 拦截双写 | 独立任务栏按钮/Alt+Tab；`ShutdownMode.OnLastWindowClose` 默认保证关单窗不死 |
| **移到新窗口** | 把当前窗口的活动会话控制器（`PersistentSession` + 草稿 + 待审批 + 队列）整体移交新窗口：旧窗 UI 卸载会话（不 Dispose 协调器）→ 新窗 Attach 同一实例；原窗回到概览/空态或关闭 | 进程内对象移交，无 RPC 重启；成本集中在 MainWindow partial 拆分 | 不产生新租约（租约随对象走）；审批弹窗 owner 需重挂 |
| **复制只读视图** | 新无主窗口以只读模式绑定**同一** `PersistentSession`：流式/工具卡片/队列可见；输入、停止、审批、重命名全部禁用；标题加"只读"后缀；`ShowInTaskbar=true` | `SnapshotChanged` 已多播；无需新协议 | 关闭写窗 → 只读视图失去数据源：应提示"会话已关闭"并禁用视图或自动关闭（PoC 验收项） |
| **fork 到新窗口** | 当前会话在指定 entry 上 `ForkAsync` → 得到子会话（新租约，Pi 进程同进程移交）→ 子会话在新窗口打开，父会话留在原窗 | `PersistentSessionManager.ForkAsync` 现成；仅 Idle 可 fork（运行中需先停止） | 最干净的第三入口；fork 后两窗各自独立写 |

最大化/最小化/Alt+Tab/任务栏为普通无主窗口能力（§4.2-4.3），四者全部满足"真窗口"标准，不是 dock 弹层。

## 9. 进程模型：每会话子进程 vs 共享代理（任务书第 4 问）

"共享代理"若指共享 Pi 进程：Pi RPC 一个进程一个会话（runtime-baseline 实测事实），不可行，不比较。可行的两种：本仓每会话一个 Pi 子进程（A/C 下不变）与"共享协调器服务"（进程内 C# 层集中，§5 方案 C）。

| 维度 | 每会话 Pi 子进程（本仓现状） | 共享代理（进程内集中服务） |
|---|---|---|
| 资源 | N 会话 = N 个 node.exe；内存随会话数线性（未实测具体数值，不编造） | Node 数不变；多一层 C# 服务与路由代码 |
| 崩溃 | 单会话 Pi 崩 → 本会话 Faulted，其他会话不受影响（`SessionFaultKind.ProcessExited` 已按会话处理） | 服务代码 bug 影响全部会话；Pi 子进程崩溃仍按会话隔离 |
| 孤儿进程 | 应用进程死亡时：本仓正常路径为有界等待+`Kill(entireProcessTree:true)`；**崩溃路径下子进程是否因管道 EOF 自行退出未实测**（推断：Pi RPC 读 stdin，EOF 应退出；必须实验确认，见 §12 V2） | 同左 |
| 持久化锁 | `SessionLease` FileShare.None，跨进程互斥已实现；崩溃后 OS 释放句柄，租约自动可重取 | 不变 |
| 重启恢复 | `OpenAsync` 重读 `get_entries` + `WasActive`/末条 user entry → interrupted 判定（`PersistentSessionManager.cs:286-291`）；按会话独立恢复 | 不变 |
| 写冲突 | 租约拦截双写；SQLite 元数据多连接（Pooling=false, 10s busy）低频写场景够用，**跨进程争用未实测** | 同左 |

**多进程 IPC 身份与访问控制**（方案 B 时才引入）：Fufucode 进程间唯一必需通信是"把新窗口请求转给已有实例"（单实例转发）。可行形态为命名管道 + 进程 PID 校验（`GetNamedPipeClientProcessId`）；同用户会话内运行，威胁模型限于同用户进程——与现状（Pi 子进程经匿名管道继承句柄，外部进程拿不到句柄）一致，不引入提权或跨用户访问。此段为设计约束陈述，**方案 B 未实施前不需要任何 IPC 代码**。

## 10. 最小双窗 PoC 方案（设计稿，未实施）

范围：只验证"新窗口（新会话）+ 双窗共存 + 关闭/恢复 + 租约拒绝 + 只读视图"。不做"移到新窗口"（依赖 partial 拆分）。

1. `SessionWindow : Window`（无主，`Show()`；标题=会话名；`ShowInTaskbar=true` 默认）：内含 ChatPage + 自建 `MainViewModel`（复用 `AttachSession` 的装配思路，剪掉工作台/终端/浏览器）。
2. `SessionWindow.OpenAsync(AppOptions)`：自建 `RealSessionHost`（同 `AppDataLayout` 根）；mock/real 双模式沿用 `AppOptions.Mock`。
3. 只读视图：`SessionWindow(isReadOnly:true)` 绑定传入的 `PersistentSession`，禁用 Input/停止/审批/重命名，订阅快照。
4. 入口：概览页/命令面板加"在新窗口打开"（先限 mock 或无凭据路径，真实模型验收按惯例另列）。
5. 显式设置 `ShutdownMode.OnLastWindowClose`（与默认一致但写明意图）。

**关闭/恢复验收表**：

| # | 场景 | 预期（全部需实机验证后才能标通过） |
|---|---|---|
| A1 | W2 打开新会话并跑一轮 | 流式仅在 W2；W1 不受扰；任务栏两个按钮同组；Alt+Tab 两项 |
| A2 | 在 W3 以只读绑定 W1 会话 | 内容实时同步；输入/停止/审批禁用；W1 关闭后 W3 提示会话已关闭 |
| A3 | W2 对 W1 已持会话 `ResumeAsync` | 租约拒绝（InvalidOperationException），UI 显示"会话已由另一窗口打开"，不新建 |
| A4 | 关闭 W1 | W2 存活、进程不退出；W1 草稿已存；W1 的 Pi 子进程退出 |
| A5 | 关闭 W2（最后一窗） | 应用退出；无 node.exe 残留（若残留即 §12 V2 触发） |
| A6 | 重启应用恢复 | 概览可恢复 W2 会话，`get_entries` 重建，interrupted 判定正确 |
| A7 | 最小化 owner / 恢复 / 跨显示器拖动 | 无主窗口互不隐藏（owned 才有连坐）；DPI 按最近显示器 |
| A8 | 双开 Fufucode 进程同会话 | 第二进程租约拒绝（现状能力，回归确认） |

## 11. 浏览器类比与"harness 现状"（任务书第 6 问）

- **小样本核实**（样本=Avalonia 12.1.3 源码/程序集 + 本仓 + MS 文档，见 §2）：Windows 桌面 harness（Win32 窗口模型 + Avalonia）对"一进程多顶级窗口、每窗任务栏/Alt+Tab 独立项、跨显示器、独立最小化恢复"是一等公民能力（§4），本仓唯一缺口是 UI 层只有单 MainWindow（§3.1）。
- **浏览器体验 ≠ 浏览器进程架构**：Chrome/Edge 的多窗口体验来自"同一浏览器进程内创建多个顶级窗口（Chromium 中窗口属于同一 browser process，每个窗口独立的 HWND 与 shell 表示）+ 每个内容页再有独立渲染进程"。体验要素（每窗独立任务栏项/Alt+Tab/最小化）由**窗口系统**提供，与"每窗一个进程"无关；浏览器拆多进程是为了渲染器沙箱与故障隔离，不服务于窗口语义。对应到本仓：会话级隔离已由"每会话一个 Pi 子进程"承担（内容隔离对应渲染进程），窗口级无需再拆 app 进程——这正是方案 A 的架构对应关系。此为架构解释，非对任一具体浏览器版本的实现断言。

## 12. 最小验证方案（需另行授权的实验，本报告未执行）

| ID | 实验 | 判定 |
|---|---|---|
| V1 | PoC 构建 + §10 A1-A8 手工实机走查 | 全部通过且无残留 |
| V2 | 孤儿进程探针：应用运行中 `taskkill /F` 杀 Fufucode 进程，`tasklist` 查 node.exe | Pi 子进程 ≤ 数秒内退出（stdin EOF）；否则需 Job Object 方案 |
| V3 | SQLite 争用探针：两个 Fufucode 进程同时做元数据写（建会话/存草稿） | 无 `SQLITE_BUSY` 超时异常；否则评估 WAL/写队列 |
| V4 | 任务栏分组/Alt+Tab 截图取证：双窗 + 双进程各一组 | 记录默认分组实际表现（验证 §4.2 推断） |
| V5 | Pi 会话文件并发写探针（仅取证，不作为产品路径）：两个 Pi 进程绑同一会话文件各写 entry | 取证 JSONL 是否损坏；结论仅用于文档"为何必须租约" |

## 13. 实施影响（供主 agent 排期参考）

- **新增**：`SessionWindow`（+ 只读变体）、概览/命令面板"新窗口"动作、`ShutdownMode` 显式化；均不触碰 `Fufucode.Core/Fufucode.Pi` 公共契约。
- **重构（第二步）**：从 `MainWindow.*.partial` 提取会话装配（AttachSession/AttachRealSession/审批 host/通知）为可复用控制器，"移到新窗口"才需要；`ExtensionUiDialogHost` owner 支持重挂。
- **窗口附属资源随窗走**：终端（ConPTY）、浏览器（WebView2）生命周期挂在所属窗口关闭路径（现 OnClosing 模式复制）。
- **独立进程（方案 B）触发条件建议**：出现真实的"UI 崩殃波及全部窗口"事故、或单会话工作台内存证明需要进程级回收、或需要与方案 B 绑定的单实例/文件关联体验。三者皆无时 A 已满足需求。
- 系统级任务栏增强（缩略图工具栏、进度徽标、`FlashWindow` 提醒、窗口级 AUMID 分组）：低优先，不纳入必做（按任务书）。

## 14. 风险

1. `MainWindow` partial 拆分回归面大（1057 行 + 4 个 partial），PoC 应绕开而非先拆。
2. `App.SettingsStore`/`_currentOptions` 静态单例假设：多窗口同进程成立，但新增"每窗口不同 AppDataRootOverride"会破坏它——PoC 不引入。
3. 审批弹窗 owner 固定：会话移动后弹窗可能挂错窗口（中风险，进 §13 第二步）。
4. 只读视图与写窗的事件风暴共享 UI 线程：长会话流式时第二窗渲染成本未测（低风险，标记观察）。
5. 默认任务栏分组按进程：用户若期望"每窗独立分组"需 AUMID P/Invoke（非 Avalonia 内置），属可选增强。
6. SQLite 默认回滚日志在跨进程写争用下的表现未实测（V3 前不得假设无阻塞）。

## 15. 开放问题

1. 跨进程只读视图是否有不引入写风险的通道（Pi 无只读模式/会话锁文档；V5 仅取证）。
2. Pi 子进程在父进程崩溃后的确切行为（stdin EOF 退出？）——V2。
3. "新窗口"是否需要支持与主窗不同项目（跨项目窗口）及扩展启用列表按窗隔离（`UserExtensionPaths` 目前 per-manager）。
4. 单实例协议与 `--new-window` 命令行转发的产品需求（方案 B 前置，A 下可后置）。
5. `WindowClosingBehavior.OwnerAndChildWindows` 与审批弹窗（owned）在 owner 非正常关闭时的清理次序实机表现。

## 16. 证据级别总表（抽查索引）

| 结论 | 级别 | 依据 |
|---|---|---|
| Avalonia 12.1.3 多窗口 API 齐 | 源码事实 | tag `8eeda4f` 各文件行号（§4.3 表） |
| ShutdownMode 默认 OnLastWindowClose | 源码事实 | `ShutdownMode.cs` 枚举序 + 本仓未设置 |
| 本仓单窗口/每会话一 Pi 子进程/租约 | 源码事实 | §3 行号 |
| owned window 无任务栏按钮、随 owner 最小化隐藏 | 官方文档事实 | Taskbar/Window Features 原文（§4.1） |
| WS_EX_TOOLWINDOW 不进 Alt+Tab | 官方文档事实 | Extended Window Styles 原文（§4.1） |
| 默认任务栏按进程分组 | 推断（文档启发式+Avalonia 无 AUMID 引用） | §4.2/§4.3，本机未实测 |
| 两无主窗口各自入 Alt+Tab/任务栏 | 文档推断（本机未实测，PoC A1 取证） | §4.1 |
| 父进程崩溃后 Pi 自行退出 | 未证实（推断 stdin EOF 退出） | §9、V2 |
| SQLite 跨进程争用行为 | 未证实 | §9、V3 |
| Pi 会话文件被双进程并发写 | 未证实（禁止路径） | §7、V5 |
| 本报告所有 UI/性能结论 | 未实测 | 全文无实测；§2 声明 |
