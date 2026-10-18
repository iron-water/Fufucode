# T1-TERMINAL 交付记录：底部终端深度体验（首字符 / IME / 剪贴板 / 历史）

任务书：`docs/tasks/hourly-T1-terminal-experience.md`｜难度 L3｜受管特种兵独立功能包
工作树：`C:/Projects/Fucode/worktrees/t1-terminal`｜分支 `agent/t1-terminal`

## 1. 状态

| 项 | 值 |
|---|---|
| 状态 | 完成（模块内实现 + 定向测试绿；原生验收项如实标未验） |
| 起点 HEAD | `c86254fecbe79c7bcd393c49141d51a37859385e` |
| 代码/测试提交 | `d80c345c4c6e5d5743482121d268a7258e3aff2a`（实现 + 定向测试工程 + 本记录初版） |
| 记录修正提交 | 本行 SHA 修正所在的收尾提交，见 `git log -1 --format=%H` |
| 集成 | **未集成**。主区（`C:\Projects\Fucode\Fufucode`）未改动，未推送 |
| 全量自测 | **未运行**（按 v3 战略与任务书要求延后，1480 断言不属本轮门禁） |

## 2. 改动清单

允许路径内改动（任务书 §1）：

| 文件 | 改动 |
|---|---|
| `src/Fufucode.App/Views/TerminalDockView.axaml.cs` | 停靠页级文本捕获（焦点+字符原子化）、停靠页空白左键聚焦输出面、IME 组合预览刷新、右键菜单动作与可用性同步 |
| `src/Fufucode.App/Views/TerminalDockView.axaml` | 输出面右键菜单（复制/粘贴/清屏）、输入条 IME 组合临时预览徽标与样式 |
| `src/Fufucode.App/Views/TerminalScreenControl.cs` | 新增 `CaptureInput`（同一步聚焦+写入一个字符）与 `FocusForInput` 输入接收接缝 |
| `src/Fufucode.App/Features/TerminalExperience/TerminalCompositionState.cs` | 新增：IME 组合预览状态（尾部孤立代理项不渲染，避免半个字符） |
| `src/Fufucode.App/Features/TerminalExperience/TerminalControlSequences.cs` | 新增：本地清屏控制序列常量（CSI 2J/3J/H） |
| `src/Fufucode.App/Features/TerminalExperience/TerminalExperience.SelfTest.cs` | 新增：`InternalsVisibleTo("Fufucode.App.Terminal.Tests")`（与 Browser 轨道同做法） |
| `tests/Fufucode.App.Terminal.Tests/**` | 新增独立定向测试工程（未注册到共享 SelfTestRunner，也未加入 `Fufucode.sln`，与 Browser/Skins/Aero 测试同惯例） |
| `docs/records/T1-terminal-experience.md` | 本记录 |

未触碰：`MainWindow.Workspace.cs`、`MainWindow.axaml`、`MainWindow.*`、`ViewModels/TerminalDockViewModel.cs`、`Infrastructure/*`、共享外壳与其它轨道文件。

> 说明：任务书写 `src/Fufucode.App/Features/Terminal/**`，仓库实际目录名为 `Features/TerminalExperience/`，本轮改动均落在该目录内。

## 3. 功能实现与验收对照

### 3.1 首字符丢失（echo → cho）

**实测到的机制（无头，可复现）**：点击输出面 → `TerminalScreenControl.OnPointerPressed` 同步 `Focus()` → 随后到达的字符由输出面自己的 `TextInput` 处理器写入 shell，**首字符不丢**。也就是说，纯“点输出面再输入”的路径在无头管线上本来是正确的。

**本轮修复的是它旁边那一类真丢字的路径**：字符到达时输出面还不是焦点元素（焦点停在停靠条按钮/页签，或焦点切换尚未落到输出面）。原实现下这些字符无人接收，直接丢失。

- `TerminalScreenControl.CaptureInput(text)`：**同一步**内 `Focus()` + `SendRaw(text)`，把“焦点确立”和“字符捕获”做成一个原子操作。
- `TerminalDockView` 在停靠页根节点挂 `TextInputEvent` 隧道捕获：来源不是 `TextBox`、且输出面未聚焦时，交给 `CaptureInput`；命令框/查找框自己的文本输入一律放行，不抢占、不重复写入。
- `TerminalDockView` 在停靠页空白处（页签条留白等）左键按下时把焦点交给输出面；按钮、页签、文本框保留各自焦点语义。

**红绿证据（同一测试工程，改动前后）**

| 断言 | 关掉本轮捕获/聚焦路径 | 打开后 |
|---|---|---|
| `terminal-first-char-with-dock-focus-reaches-shell` | FAIL（`tmp/t1-red-capture.log`） | PASS |
| `terminal-click-dock-blank-focuses-surface` | FAIL（`tmp/t1-red.log`） | PASS |
| `terminal-first-char-after-click-reaches-shell`（回归护栏） | PASS | PASS |

**诚实边界**：E7-NATIVE 原始 `echo→cho` 截图是**原生**观察（`docs/records/failure-registry.md` 的 `E7-NATIVE-TEXT-FOCUS-1`），本轮**没有**在原生环境复现其确切时序，因此不宣称“已归因到唯一根因”。本轮交付的是：无头可复现的那类丢字路径已修复并有红绿证据；原生侧仍需按原验收者方式复验（逐键/注入/粘贴、普通 TextBox 对照）。

### 3.2 中文输入法（IME）

- 组合期（`TextPresenter.PreeditText` 非空）输入条右侧显示临时预览徽标「组合中：<组合文本>」，提交/取消后自动消失；命令框自身的文本与光标不受影响。
- `TerminalCompositionState` 在预览中**不渲染尾部孤立的代理项**，避免中文/emoji 组合过程出现半个字符；组合提交后由输入框正常把完整文本并入命令草稿（无重复写入）。
- 组合期回车继续由既有 `ImeEnterGuard` 吞掉，不误发送命令（本轮补了真实控件回归断言）。
- **未验证**：Windows 原生 IME 候选框跟随光标本就是 `TextBox` 的平台行为，本轮只有无头证据；原生候选框跟随、真实中文输入法提交需原生验收。

### 3.3 剪贴板复制/粘贴与右键菜单

- Ctrl+C：有选区复制冻结选区文本；无选区发送 SIGINT（`\u0003`）。既有实现，本轮补断言。
- Ctrl+V / Shift+Insert：把系统剪贴板文本归一化（CRLF→LF、去 NUL）后并入命令草稿，不写 shell、不自动执行。既有实现，本轮补断言。
- 新增右键菜单：**复制**（仅在有选区时可用）/ **粘贴** / **清屏**。
  - 复制、粘贴与键盘动作等价（复用同一实现）。
  - 清屏为**纯本地**操作：向本机缓冲区写入 `CSI 2J / 3J / H`（清屏 + 清滚动缓冲 + 光标归位），不向 shell 发送任何字节。
- **未验证**：真实系统剪贴板往返（本轮用 `ClipboardWriterForTesting` / `ClipboardReaderForTesting` 接缝）；真实右键弹出（无头只验证菜单结构与动作，未验证原生鼠标右键弹出）。

### 3.4 命令历史与导航

既有 `TerminalCommandHistory` + `TerminalDockView.HandleHistoryKey` 已实现；本轮补断言：空历史上下键无副作用、上/下切换、到顶/到底停住、返回未发送草稿、导航后光标落在文本末尾、历史按页签隔离、输出面上的方向键仍发给 shell。

## 4. 验证记录（本轮实际执行）

```
dotnet build tests/Fufucode.App.Terminal.Tests
  → 已成功生成。0 个警告，0 个错误

tests/Fufucode.App.Terminal.Tests/bin/Debug/net10.0/Fufucode.App.Terminal.Tests.exe
  → 全部通过：39 项断言（连续 3 次运行均 39/39，日志 tmp/t1-green.log、tmp/g-1..3.log）
```

覆盖断言分组：首字符/焦点捕获 9 项、剪贴板与右键菜单 12 项、命令历史 10 项、IME 组合 6 项，另含 2 项工装前置断言。

**未运行项（如实标注）**

- App 全量 `--selftest`（1480 断言）：按任务书延后，未运行；因此 `src/Fufucode.App/SelfTest/TerminalSelfTest.cs`、`TerminalIntegrationSelfTest.cs` 中既有终端自测本轮**未复跑**（改动集中在同一视图，属未验证风险）。
- 原生 ConPTY 真机交互、真实 IME、真实剪贴板、原生指针/DPI：未验证。
- 未运行 1480 断言之外的长时间挂机/打包验证。

## 5. 工装说明（非产品缺陷，供后续复用）

新建测试工程时遇到 `No precompiled XAML found for avares://Fufucode.App/Styles/FufucodeTheme.axaml`。根因在 Avalonia：
`AssemblyDescriptorResolver.GetAssembly(name)` 用**已加载程序集名的最短前缀匹配**（`src/Avalonia.Base/Platform/Internal/AssemblyDescriptorResolver.cs`）。测试程序集名 `Fufucode.App.Terminal.Tests` 同样以 `Fufucode.App` 开头，若产品程序集尚未加载就会被命中，导致 avares 资源解析到错误程序集。

规避方式（已写入测试 `Program.Main` 首行）：解析前先显式触达产品类型（`_ = typeof(TerminalScreenControl).Assembly;`），保证正确的 `Fufucode.App` 程序集已在解析前加载。这是测试工装问题，不是产品缺陷，未修改产品代码规避。

## 6. 未完成项与接线需求

1. **原生复验（必须）**：`E7-NATIVE-TEXT-FOCUS-1` 原始 `echo→cho` 观察尚未在原生环境复现与归因；本轮修复覆盖无头可复现的相邻丢字路径，原生逐键/注入/粘贴与普通 TextBox 对照仍待原验收者执行。
2. **原生 IME**：候选框跟随光标、真实中文组合提交需原生验收；无头只证明预览状态与组合期回车守卫。
3. **真实剪贴板/右键弹出**：本轮用测试接缝，原生系统剪贴板与鼠标右键菜单弹出未验证。
4. **共享入口**：粘贴动作在真实 App 里由共享 `terminal.paste` 动作执行（`MainWindow.Shortcuts.cs` 的终端作用域路由），本轮未改共享外壳；“粘贴至光标处”在草稿语义下等价于追加到当前未提交草稿末尾，未实现按命令框插入符中间插入（那需要改共享动作或 ViewModel，超出本包允许路径）。
5. **全量自测**：1480 断言与既有终端自测未复跑，集成前建议由主 agent 在收编后统一执行。
6. **收编方式**：本包为独立工作树成果，未集成、未推送；由主 agent 决定收编或返工。
