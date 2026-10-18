# P1 桌面主路径收口 — 执行会话交付记录

日期：2026-10-11。任务书：`docs/tasks/hourly-P1-desktop.md`（难度 L3，推荐 `group/auto-qfmodel`）。
边界：`docs/tasks/execution-contract.md`、`docs/tasks/session-handoff-protocol.md`。

## 0. 开工身份卡

| 项 | 值 |
|---|---|
| cwd / 工作树 | `C:\Users\mio\.codex\worktrees\1df9\Fufucode` |
| Git 状态 | detached HEAD（非分支，未同步 main、未集成） |
| 起点 SHA | `c58533d71d1af67724529f5f8cd8f024acb3f634`（main `5dce651` 后的任务书提交） |
| 代码交付 SHA | `8c9794c636ffde8a65682ed32c4fd97867ef331c`（fix(library): stack entries and detail in narrow docks） |
| 提交 | 代码提交 `8c9794c`；其后一条仅本文档的记录提交（自引用，故不写自身 SHA） |
| 允许路径 | `src/Fufucode.App/Features/{Shortcuts,Storage,Library}/**`、对应独立 tests、SelfTest 中本模块已有测试文件、本文档 |
| 未触碰 | `MainWindow*`、`Integration/**`、公共动作、Core、Settings、Styles（主控唯一持有） |
| 模型纪律 | 全程本机合成夹具与本地文件，零云模型调用；未使用任何高价模型试跑 |

## 1. 结论摘要

- **V1（Pi/OMP 后端行为）：原生补验通过。** 6 项行为在真实窗口、真实 Pi/OMP 进程上取证。
- **V2（快捷键 / 存储 / 资料库）：原生验收通过。** 三块均在真实 UI 上操作并核对落盘文件与剪贴板原文。
- **V3（资料 → 聊天接线）：未接线，属有效交付。** 独立页面行为已验收，引用文本已进剪贴板，但**不会自动插入聊天输入框**。接线补丁与接口需求见 §6，由主控实施。
- **本会话整体状态：模块交付待接线 / 提交待验收。不自称 P1 整项验收。**
- 发现并定向修复 1 个模块内缺陷（资料库窄停靠栏布局），见 §5。

## 2. 原生取证工具与已知限制

工具：`node_repl` 中 `const { sky } = await import("@oai/sky")`，对真实 `Fufucode.App.exe` 窗口做激活、截图、坐标点击、按键、输入、滚动。

实测限制（如实记录，未用 headless 冒充原生）：

- Avalonia 元素的**索引点击**一律失败（`element NNN has no cached bounds` / `not available in cached app state`）→ 全部交互改用「最新 screenshotId + 窗口相对坐标」。
- `sky.drag()` 所有参数形状均报 `from.x must be a finite number` → 不可用。因此**右 Dock 宽度未用拖拽改变**，改用布局持久化文件 `tmp/p1-native/v2/project/.fufucode/ui.json` 的 `layout.rightWidth` 设定 320（默认）与 1000（宽）两种状态后重启进程验证。
- `perform_secondary_action` 仅支持 Raise / Scroll Up-Down-Left-Right / Expand / Collapse，**不能 Invoke 按钮**。
- AX 树偶发 `accessibility: null`，重试即恢复。
- 弹出菜单是独立截图（如 `screenshot-1`，134x266），坐标须用该截图自身的 screenshotId 与相对坐标。
- 一次点击后 screenshotId 会失效（`unknown screenshotId`），须重新 `get_window_state` 取新截图。

夹具启动（本机 HTTP 合成流，零云调用）：

```powershell
$env:D1_PI_NODE='C:/Projects/Fucode/Fufucode/runtime/node/node.exe'
$env:D1_PI_CLI='C:/Projects/Fucode/Fufucode/runtime/pi/node_modules/@earendil-works/pi-coding-agent/dist/bundle/cli.js'
$env:D1_OMP_EXE='C:/Users/mio/.bun/bin/omp.exe'
node tmp/p1-native/v2-fixture.mjs     # 资料库/快捷键/存储用；隔离目录 tmp/p1-native/v2/{project,data}
node tmp/p1-native/slow-fixture.mjs   # 慢流（1 段/秒，共 40 段）用于停止取证；隔离目录 tmp/p1-native/run
```

## 3. V1 — Pi/OMP 后端原生补验（通过）

| # | 行为 | 原生操作 | 取证结果 |
|---|---|---|---|
| 1 | 审批「拒绝」不产生写入 | 会话内请求写文件 → 审批弹窗点「拒绝」 | `Test-Path` 目标文件为 `False`，无写入 |
| 2 | 再次发送并「允许」→ 真实写入 | 同会话再次发送 → 点「允许」 | 文件出现，大小 18 B，内容与请求一致 |
| 3 | 停止后无后续执行 | 慢流回合进行中点「停止」 | 时间线冻结在 seq 18（夹具本应输出到 seq 40），等待 10 s 无新增条目 |
| 4 | 取消后端选择 | 后端选择对话框点「取消」 | 会话列表无新增条目，无新会话产生 |
| 5 | 缺失后端不静默回退 | 指向不存在的后端可执行文件 | 返回明确错误文案，未回退到其它后端 |
| 6 | OMP 后端同套流程 | 重复 1/3/停止后再发送 | 拒绝后 `ui-approved.txt` SHA256 `46C11096F9A7FA22761BABDD5ACD18397B54A53F089F1D6E823F97D361C19C9C` 与 mtime 均不变；慢流停止冻结在 seq 19；停止后再次发送成功 |

注：以上为本机合成夹具 + 真实 Pi/OMP CLI 进程，未调用任何云端模型；`tmp/p1-native/run` 夹具目录未纳入 Git。

## 4. V2 — 快捷键 / 存储 / 资料库（通过）

### 4.1 V2-a 键盘快捷键（`Features/Shortcuts`）

- 设置 → 键盘快捷键：真实动作目录渲染（非占位列表），中文关键词搜索正常。
- 录制 `Ctrl+B` → 行内即时显示新组合，状态文案「已绑定『底部面板』→ Ctrl+B」。
- **未保存即生效**：绑定后底部终端区立即隐藏（原生截图核对）。
- 保存 → `tmp/p1-native/v2/data/keybindings.json` 落盘 `{"toggle.bottom":["Ctrl+B"]}`。
- 「全部恢复默认」→ `Ctrl+J` 行为回归，落盘文件为 `bindings: {}`（当前实测文件内容：`{"version":1,"bindings":{}}`）。

### 4.2 V2-b 存储清理（`Features/Storage`）

- 9 个类目均明确标注「参与清理 / 不参与清理」。
- 「重新计量」→ 11 文件 / 96.5 KB。
- 构造风险样本：过期日志（mtime −40 天，9 B）与一个指向库外文件的 junction（目标 `tmp/p1-native/outside/protected.txt`）。
- 预览：「1 个目标，预计释放 9 B」+「1 个条目未能统计」（junction 未被计入可释放量）。
- 执行清理后原生核对：**过期日志删除；junction 本身保留；junction 指向的外部文件完好未删**；标注不参与清理的类目文件仍在。

### 4.3 V2-c 资料库（`Features/Library`）

打开路径（原生）：右 Dock「+」(1381,74) → 弹窗 `screenshot-1` 内「资料库」(67,183)；或 `Ctrl+K` 命令面板输入「资料库」+ 回车。两条路径均已实测可用。

- **导入**：系统文件对话框输入完整路径 → 导入成功。条目元数据（AX 树原文）：
  `Id=52b056bced914c1c8d3bdaa49a145207`、`Kind=File`、`StorageMode=Copy`、
  `ContentHash=2010b14776b4e8609ab59178f7e95da7b5101965d3c6c4b99e5447936f0a4e43`、
  `TextState=Indexed`、`VersionCount=1`、`HasLocalCopy=True`、`Status=Ok`。
- **副本落盘核对**：`tmp/p1-native/v2/data/library/copies/52b056bced914c1c8d3bdaa49a145207/v1/sample-note.txt`，磁盘 SHA256 `2010B14776B4E8609AB59178F7E95DA7B5101965D3C6C4B99E5447936F0A4E43`，与库记录一致。
- **全文检索（含负向对照）**：`NEEDLE-42` → 「共 1 条匹配」；`ZZTOP-NOMATCH-9999` → 列表空 + 提示「还没有资料条目」。负向不命中证明检索真实生效，非恒真。
- **引用生成与复制**：点「生成引用」→ 状态栏「已生成引用（含来源与版本）；聊天接线后可直接插入输入框。」，详情区出现「聊天引用（可插入输入框）」。`Get-Clipboard -Raw` 原文：

  ```text
  > 资料《sample-note.txt》· 来源：C:\Users\mio\.codex\worktrees\1df9\Fufucode\tmp\p1-native\v2\project\sample-note.txt · 版本 2010b147
  > P1 V2 sample document line one.
  > Second line with keyword NEEDLE-42 inside.
  > Third line.
  >
  ```

- **剪贴板写入归属可证**：先 `Set-Clipboard -Value "SENTINEL-WIDE"` 写哨兵，再原生点击「生成引用」，随后读回为上述引用文本 → 证明内容确由该次点击写入，而非历史残留。
- **键盘焦点**：Tab 序可遍历「条目 → 新标签输入框 → 添加标签 → 同步来源变化 → 生成引用 → 删除条目」（修复前 320 宽度下「生成引用」被跳过，见 §5）。
- **窄/宽两种停靠栏宽度**（原生，两种宽度分别重启进程取证）：
  - `rightWidth=320`（默认，`WorkbenchLayoutState.RightWidth = 320`）：上下堆叠布局，4 个操作按钮可见且「生成引用」点击成功。
  - `rightWidth=1000`：仍为两列布局（列表列 + 右侧详情），详情区显示标题/摘要/来源/标签/预览正文/版本历史（`v1 · 2010b147 · 导入 · 2026-10-11 12:22`）；选中条目后点「生成引用」，状态栏文案与剪贴板内容同上，无宽模式回归。
  - 未选中条目时点「生成引用」不写入剪贴板（哨兵 `SENTINEL-CLEARED` 保持），与页面底部既有约束「未选中的条目不会发送给模型」一致。

### 4.4 代码事实（用于判定接线归属）

`LibraryPageView` 自带 `ILibraryFilePicker`（基于 `TopLevel.StorageProvider`）并自订阅 `CitationCopyRequested` 写剪贴板 → **导入与复制不依赖宿主**。宿主 `src/Fufucode.App/Views/MainWindow.Workspace.cs:71-79` 只做 `RegisterOptional` + `Attach`/`Detach`。

## 5. 缺陷登记（本会话唯一代码改动）

**D-P1-1｜资料库页在窄停靠栏下操作按钮不可达**

- 现象：右 Dock 为默认宽度（320/360）时，「添加标签 / 同步来源变化 / 生成引用 / 删除条目」完全不可达；原生 Tab 序跳过「生成引用」，按钮无渲染尺寸。
- 根因：`LibraryPageView.axaml` 条目区固定 `ColumnDefinitions="340,*"`，页头固定 `ColumnDefinitions="*,Auto"`。340 列表列 + 12 间距 + 32 左右留白已超过 320 容器宽度，详情列被压成 0 宽。
- 修复（最小改动，仅 `Features/Library` 内）：
  - `LibraryPageView.axaml`：为 `HeaderGrid`/`HeaderTitle`/`HeaderActions`/`EntriesGrid`/`EntryListHost`/`EntryDetail`/`DetailHeader`/`DetailSource`/`DetailNote` 添加 `x:Name`，无其它结构改动。
  - `LibraryPageView.axaml.cs`：`OnSizeChanged` → `ApplyNarrowLayout(Bounds.Width)`；阈值 `twoColumnMinimum = 720`；窄模式两列改单列、页头动作换到第 2 行、列表行 `120,*`、`RowSpacing=8`、长路径与备注改单行截断（`NoWrap` + `CharacterEllipsis`）；宽模式还原 `*,Auto` / `340,*`，并用 `RowDefinitions.Clear()` 精确还原 XAML 隐式单行 Star（显式写 `Auto` 会让列表不再撑满高度，已避免）。同一宽度状态内幂等。
  - `tests/Fufucode.App.Library.Tests/UiSmoke.cs`：新增窄宽度回归（`window.Width` 320 → 960 双向）与 `ActionButtonsReachable(view)`，要求 4 个按钮 `IsVisible && Bounds > 1` **且** `TranslatePoint` 后完整落在视图可视区内（仅判宽度会漏掉横向溢出被裁）；失败时打印诊断。
- 断言非恒真证明：临时 `git stash` 回退 axaml 修复后该断言 **FAIL**（`窄宽度下标签/引用/删除按钮仍可达`），恢复修复后 PASS。
- 中途加强断言曾暴露纵向溢出 17 px（按钮 origin y=544/576，但「添加标签」底边到 737 > 视口 720），据此把列表行 220→120、长文本单行截断。最终 320x720 下按钮 origin 为 (144,544)/(16,576)/(124,576)/(204,576)，全部在界内。
- 其它模块（Shortcuts/Storage 页面）在窄 Dock 下是否有同类固定列问题：**本次未检查**，留给主控。

## 6. V3 — 资料 → 聊天接线需求（提交主控，本会话未实施）

现状：`LibraryViewModel.CopyCitation()`（`Features/Library/LibraryViewModel.cs:408-418`）生成 `citation.Markdown` → 赋 `CitationText` → 触发 `CitationCopyRequested` → 视图写剪贴板。事件注释已声明「宿主接线：把引用文本写进输入框或剪贴板。模块不直接操作聊天会话」。缺的是**自动插入聊天输入框**这一环。

建议补丁（全部落在主控持有的 `MainWindow*`，本会话未写）：

1. 宿主在 `Views/MainWindow.Workspace.cs` 的 `RegisterOptional("library", ...)` 工厂内创建 VM 后，额外订阅一次事件，例如：

   ```csharp
   var vm = new LibraryViewModel(_libraryService);
   vm.CitationCopyRequested += text => Dispatcher.UIThread.Post(() => AcceptLibraryCitation(text));
   view.Attach(vm);
   ```

2. 落点复用既有输入框写入路径 `InputViewModel.ApplyExternalText(string)`（`ViewModels/InputViewModel.cs:462`），它已实现「空草稿直接采用；已有未发送草稿则存为待恢复文本并返回 false」的不覆盖纪律，与扩展 `set_editor_text`（`Integration/SessionUiWiring.cs:67`）、`ChatPage.EditMessage`（`Views/ChatPage.Actions.cs:28`）、侧边聊天（`Views/MainWindow.SideChat.cs:121`）一致。返回 false 时应按既有做法给时间线通知，不要静默丢弃：

   ```csharp
   private void AcceptLibraryCitation(string text)
   {
       var vm = /* 目标会话的 MainViewModel，见第 3 点 */;
       if (vm is null) return;
       if (!vm.Input.ApplyExternalText(text))
           vm.Timeline.AddNotification("资料引用已生成；因存在未发送草稿，已存为待恢复文本。");
       _chatPage.Input.Focus();
   }
   ```

3. **需主控决策的路由**：「正确的聊天会话」有两个候选——主聊天 `_chatPage`（`Views/MainWindow.axaml.cs:52`，经 `_vm`）与侧边聊天 `_sideChatPage`（`Views/MainWindow.SideChat.cs:15`，`sidechat` 停靠页）。建议默认写当前活动会话，并在资料库页显式提供目标选择（或至少状态栏回显写入的是哪个会话），避免把长引用误插到无关会话。
4. **保留既有约束**：未选中条目时 `CopyCitation` 直接 return，不产生事件（已原生验证），接线不得绕过。
5. 若希望解耦，可新增 `IChatCitationSink` 接口由宿主注入（与 `ILibraryFilePicker` 同风格）。注意：现有 `CitationCopyRequested` 已被视图用于写剪贴板，且 `UiSmoke` 断言「订阅生效且仅此一次路径」，改成双播需同步调整该断言语义，不宜只改宿主。

## 7. 实际运行的验证（本工作树，2026-10-11）

```text
dotnet build src/Fufucode.App -v quiet                          → 已成功生成，0 个警告，0 个错误
dotnet run --project tests/Fufucode.App.Library.Tests           → 全部通过：134 项断言
dotnet run --project src/Fufucode.App -- --selftest             → == 自测结束：0 项失败 ==（约 105 s）
```

原生 UI 取证见 §2–§5（真实 `Fufucode.App.exe` 窗口，非 headless）。

## 8. 未验证项（如实标注，不得当作通过）

- 真实 IME 组合态输入（快捷键录制与资料检索仅用 `type_text`/直接按键）。
- 系统 DPI / 缩放非 100% 下的布局与坐标命中。
- 多显示器、窗口跨屏拖拽后的布局。
- 真实云端模型试跑（全部为本机合成夹具；Pi/OMP 为真实 CLI 进程但模型响应为本地流）。
- 生产数据迁移、干净安装、升级路径。
- WebView2 真实页面布局。
- 鼠标拖拽改变 Dock 宽度（`sky.drag` 不可用，改用持久化文件设定后重启进程覆盖 320 / 1000 两档）。
- Shortcuts / Storage 页面在窄停靠栏下是否存在同类固定列问题（未检查）。
- 资料库「引用（非副本）」导入模式、网页链接与文本片段导入的原生端到端（本轮仅原生验收副本模式文件导入）。

## 9. 交付卡

| 项 | 内容 |
|---|---|
| 任务 | P1 = V1+V2+V3 桌面主路径收口（`docs/tasks/hourly-P1-desktop.md`，L3） |
| 状态 | **模块交付待接线 / 提交待验收**（非整项验收） |
| 工作树 | `C:\Users\mio\.codex\worktrees\1df9\Fufucode`，detached HEAD |
| 起点 → 代码 SHA | `c58533d71d1af67724529f5f8cd8f024acb3f634` → `8c9794c636ffde8a65682ed32c4fd97867ef331c`（其后为本文档记录提交） |
| 提交 | `8c9794c` fix(library): stack entries and detail in narrow docks（3 files, +103/−9）；本文档单独一条记录提交 |
| 变更文件 | `src/Fufucode.App/Features/Library/LibraryPageView.axaml`、`.../LibraryPageView.axaml.cs`、`tests/Fufucode.App.Library.Tests/UiSmoke.cs`、`docs/records/P1-desktop.md` |
| V1 | 已原生验收（6/6 通过，Pi + OMP） |
| V2 | 已原生验收（快捷键 / 存储 / 资料库三块，含落盘与剪贴板原文核对） |
| V3 | **未接线**：引用文本已进剪贴板，未自动插入聊天输入框；补丁与接口需求见 §6，属主控持有路径 |
| 缺陷 | 登记 1 项（D-P1-1 资料库窄停靠栏布局），已定向修复并加回归断言 |
| 验证 | build 0 警告 0 错误；Library.Tests 134 项通过；`--selftest` 0 项失败 |
| 未验证 | 见 §8（9 项，含真实 IME、DPI、多显示器、云模型试跑、生产迁移、干净安装、WebView2、拖拽改宽、其它模块窄宽度） |
| 未做 | 未同步/集成/改写 main，未 push，未清理 worktree，未动 `MainWindow*`/`Integration`/Core/Settings/Styles，未派下一任务 |
| 需要协调 | 是 — V3 接线（§6 第 3 点路由决策 + 第 5 点接口取舍）由主控实施 |

## 10. 停止写入声明

本会话已完成交付记录与提交，**停止写入**：不再修改代码、不再提交、不再动工作树状态（含不清理 `tmp/` 夹具目录）。等待原会话/主控的返工或验收指令；收到明确新缺陷指令时再继续。不自行开始下一任务。
