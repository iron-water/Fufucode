# BRW-1 内置浏览器对标 Codex / ChatGPT：交付记录

日期：2026-10-15。状态：模块交付待接线 → 已在工作树完成实现与定向验证；提交待主控验收。
工作树：`C:/Projects/Fucode/worktrees/brw1-browser-codex`，分支 `agent/brw1-browser`，起点 `3287950`。

## 1. 任务与范围

用户 2026-10-15 指示：内置浏览器页签"功能对比 codex 差很多，页面非常杂乱"，要求所有功能对标 Codex 浏览器能力。本任务（BRW-1）在工作树内完成四件事：

1. 内置浏览器界面重构：一体化极简胶囊地址栏 + 统一顶栏；去除二重/三重分划线与长文状态栏。
2. 全量实装【设置 → 浏览器】面板，配置原子落盘到 `<dataRoot>/browser-settings.json`。
3. Browser Use 真实接线：把 `BrowserUsePlugin` 桥到真实 WebView2 CDP 会话。
4. 验证与交付：`dotnet build Fufucode.sln` 0 警告 0 错误 + 定向测试 + 本记录 + 提交。

## 2. 改动清单

### 2.1 浏览器设置真源（新增）

| 文件 | 作用 |
| --- | --- |
| `src/Fufucode.App/Infrastructure/BrowserSettingsModel.cs` | `BrowserSettings` 记录 + `BrowserSitePermission`；所有枚举值写入前经 `Normalize()` 收敛到白名单；`FromLegacy` 迁移旧 `WorkbenchAppearance`。 |
| `src/Fufucode.App/Infrastructure/BrowserSettingsStore.cs` | 原子持久化 `<dataRoot>/browser-settings.json`；临时文件 + 替换；损坏/更新版文件保留原文件并如实报告，不静默覆盖。 |
| `src/Fufucode.App/Infrastructure/BrowserSettingsHost.cs` | 进程内唯一宿主：设置页与浏览器读取同一份 store。 |
| `src/Fufucode.App/Infrastructure/Browser/BrowserDownloadHistory.cs` | `<dataRoot>/browser-downloads.json`；只记录真实发生的下载，最多 200 条。 |
| `src/Fufucode.App/ViewModels/BrowserSettingsViewModel.cs` | 设置页投影；每次改动归一化 + 原子落盘 + 广播；未连接数据根时如实提示"未落盘"。 |

### 2.2 浏览器界面重构

- `src/Fufucode.App/Views/BrowserView.axaml`：删除底部文字状态栏（`ModeText`/`StatusText`/缩放簇）与二重分划线；改为单个 `Border.omniBar` 一体化胶囊，内含 锁/不安全图标 + 星标 + 地址框 + 分享；顶栏统一为 后退/前进/刷新/停止 + 检查/打开文件/更多。
- `src/Fufucode.App/Views/BrowserViewModel.cs`：新增 `Security`/`ShowSecurity`/`DisplayAddress`/`IsCurrentBookmarked`，新增 `Share`、`ToggleBookmark` 命令，`ApplyShowFullUrl` 同步全页签。
- `src/Fufucode.App/Features/BrowserExperience/BrowserTab.cs`：新增 `BrowserSecurityState`（HTTPS/HTTP/无），只有真实 https/http 页面点亮锁图标；`ShowFullUrl` 关闭时 `DisplayAddress` 只显示主机名。
- `src/Fufucode.App/Views/BrowserView.axaml.cs`：接线 `ShareRequested`、`ApplyAgentControl`、`NavigateFromAgent`。

### 2.3 设置页【浏览器】卡片

`src/Fufucode.App/Views/WorkbenchSectionsView.cs` 重写 `RenderBrowserSettings`，结构与用户提供的 ChatGPT/Codex 设置页一致：

- 顶部卡片：让智能体控制内置浏览器（主开关）。
- 常规：网页 URL 打开位置、本地 URL 打开位置、显示完整网址、截图策略、清除浏览数据（缓存/Cookie/网站数据/全部）、浏览历史管理。
- 下载：默认下载位置、下载前询问保存位置、记录下载历史、下载历史记录。
- 权限与智能体控制：启用站点工具（WebMCP）、智能体浏览权限、智能体下载权限、智能体网站白名单与权限例外。
- 开发者模式：启用完整 CDP 访问权限 + 风险提示；未安装插件时禁用并给出安装指引（不渲染假开关）。

`BuildCdpRow` 的取值真源改为 `BrowserSettingsViewModel`，不再是旧 `WorkbenchAppearance` 字段。

### 2.4 Browser Use 真实接线

- `src/Fufucode.Core/Plugins/BrowserUsePlugin.cs`：动作集从 3 个扩展为 5 个标准动作——`browser_navigate` / `browser_click` / `browser_screenshot` / `browser_evaluate` / `browser_get_state`（外加既有 `browser_cdp`），每个都带参数 JSON Schema；`Bridge` 的 `{"error":...}` 载荷如实映射为失败结果。
- `src/Fufucode.App/Features/BrowserExperience/WebView2BrowserUseBridge.cs`（新增）：把动作接到真实 WebView2 —— CDP 走 `CallDevToolsProtocolMethodAsync`，evaluate/click 走 `ExecuteScriptAsync`，screenshot 走 `Page.captureScreenshot`，get_state 读真实地址/标题/加载态。
- 门禁两层：插件市场（已安装且已启用）由 Core `PluginToolRegistry` 控制；用户开关（主开关 / 完整 CDP）由桥在每次调用前再校验，未通过返回可读拒绝原因。
- `src/Fufucode.App/Views/MainWindow.BrowserSettings.cs`（新增）：组合根创建唯一设置宿主、构建 `PluginToolRegistry`、把活动浏览器视图的真实桥接入插件；首次运行时迁移旧 `WorkbenchAppearance` 浏览器偏好。
- `src/Fufucode.App/Integration/NativeSessionBootstrap.cs`：`CreateNativeSession` 可接受已构建的插件工具注册表，原生内核因此能实际消费浏览器工具。

## 3. 实际验证

| 命令 | 结果 |
| --- | --- |
| `dotnet build Fufucode.sln --no-incremental` | 成功；0 警告 0 错误 |
| `dotnet run --project tests/Fufucode.App.Browser.Tests` | 全部通过：96 项断言 |
| `dotnet run --project tests/Fufucode.App.Backend.Tests` | 全部通过：479 项断言 |
| `dotnet run --project tests/Fufucode.App.Settings.Tests` | 全部通过：66 项断言 |
| `dotnet run --project tests/Fufucode.App.Plugins.Tests` | PROOF ALL PASS (21) |
| `dotnet run --project src/Fufucode.App -- --selftest` | 自测结束：0 项失败 |
| 无头真实渲染截图（浏览器深/浅色、浏览器设置卡片） | 3 张 PNG 已生成并人眼核对；确认一体化胶囊、无文字状态栏、设置结构正确 |

定向测试覆盖（`tests/Fufucode.App.Browser.Tests/BrowserSettingsTests.cs`、`BrowserSettingsUi.cs`、`UiSmoke.cs`）：

- 设置原子落盘、重开可读回、非法枚举收敛、损坏文件不覆盖、更新版文件保留。
- 地址栏安全态（HTTPS/HTTP/about:blank）与"只显示主机名"。
- 白名单解析/格式化往返、非法主机名过滤。
- 桥门禁：主开关关闭时 evaluate/navigate 被拒；完整 CDP 关闭时 CDP 被拒。
- 端到端：插件未装时调度表为空；装好后暴露 5 个动作；未接线桥返回"尚未接线"；完整 CDP 关闭时经注册表被拒；卸载后无幽灵工具。
- 按范围清除缓存/Cookie 真实删除，且不误删 Cookie、保留 Crashpad。
- 设置卡片真实控件结构（必需行齐全、下拉/开关/按钮存在）。
- 界面：320 DIP 浅/深主题入口可达；已移除"自由导航/加载完成"长文与底部页签计数状态栏。

## 4. 未执行与未验证

- 真实 WebView2 native module self-test（`--native-module-selftest`）在本轮未运行；真实页面上的点击/截图/CDP 端到端交互、指针/IME/DPI/焦点未纳入本轮证据。
- 真实模型驱动的 Agent 浏览闭环（模型实际发出 `browser_*` 调用并完成任务）未运行。
- 旧 `WorkbenchAppearance` 浏览器字段的删除未做（保留兼容读取，避免破坏其他读它的人）；浏览器设置已是新真源。
- `Fufucode.Core.Tests` 在本工作树失败于环境缺失 `runtime/node/node.exe`（本工作树没有该运行时，主区有），与本任务改动无关；本任务对 Core 的改动仅新增 `BrowserUsePlugin` 动作。

## 5. 交付卡

```
交付卡
任务ID与软件：BRW-1 / Fufucode（Codex 受管执行会话）
工作树绝对路径、分支/HEAD、起点SHA：
  C:/Projects/Fucode/worktrees/brw1-browser-codex
  agent/brw1-browser；起点 3287950
本任务提交SHA：e9e9775（实现 24 文件）；本记录 SHA 补录见紧随其后的记录提交
改动文件：见第 2 节（12 改 + 9 新增）
实际验证命令、结果、证据位置：见第 3 节
未运行验证与原因：见第 4 节
公共接线需求：组合根已接线（MainWindow.BrowserSettings.cs）；
  真实原生页面交互与模型驱动闭环待主控/后续原生验收
状态：模块交付待主控验收（提交待验收）
原会话是否仍在写代码：否（提交后停止写入）
```
