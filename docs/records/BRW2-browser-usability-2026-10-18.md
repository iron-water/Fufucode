# BRW-2 内置浏览器可用性与 Codex 对标：交付记录

日期：2026-10-18。状态：实现完成，定向测试与原生工装通过；全量无头自测运行中，结果以 `out/selftest-full.log` 为准。

## 1. 背景

上一轮对比发现：内置浏览器是“页面查看 + 简单控制”工具，缺失下载、站点权限、弹窗 opener、搜索回退、收藏/历史落盘、标注回传、智能体审批等能力；多个设置项只有 UI 与存储，没有运行时消费者。

本轮按“先修日常可用性，再补 Codex 对标闭环”的顺序实现，未改动发布渠道、插件打包与后端协议。

## 2. 实际改动

### 2.1 地址栏与导航

- `Infrastructure/Browser/BrowserAddressResolver.cs`：新增 `Search` 目标类型；含空格/非主机文字转搜索 URL，不再拒绝；非法搜索模板回退默认模板；`javascript:`/`ftp:` 仍被拒绝。
- `BrowserSettings.SearchUrlTemplate`（默认 Google `?q={0}`）由设置页下发到每个页签；新增 `UserAgent` 设置并下发到每个 WebView2。
- 设置 `WebUrlOpenTarget=system` 时，页面内 http/https 跳转改投系统浏览器；地址栏/前进后退/刷新期间的主动导航不受影响。

### 2.2 下载

- `BrowserViewHost` 不再无条件取消 `DownloadStarting`：改为 deferral + 视图决策。
- `BrowserView` 按站点下载权限与 `PromptDownloadLocation` 选择真实保存路径；同名文件自动追加序号，不覆盖。
- 下载进度（真实字节数/终态/中断原因）进入 `BrowserViewModel.Downloads`，工具栏下载列表可取消进行中的下载。
- 完成后按 `DownloadHistoryEnabled` 写入 `browser-downloads.json`。

### 2.3 站点权限

- 新增 `Infrastructure/Browser/BrowserSiteRules.cs`：白名单主机匹配、默认浏览/下载权限、会话内权限记忆（纯逻辑，无 WebView2 依赖）。
- `BrowserViewHost` 订阅 `PermissionRequested`，用 deferral 等待 UI 决策；`BrowserView` 对摄像头/麦克风/定位/通知/剪贴板等显示真实确认窗口；后台非用户发起的请求默认拒绝。
- 站点规则为“允许”时写入 profile；站点规则为“禁止”时直接拒绝。

### 2.4 弹窗与 opener

- `NewWindowRequested` 改为 `BrowserNewWindowRequest`：视图创建新页签后，通过 `EnsureCoreAsync` 把新页签真实的 `CoreWebView2` 交回，宿主设置 `e.NewWindow`，保留 `window.opener`、`postMessage` 与 OAuth 弹窗关系。
- 原“只导航一个新页签”的路径保留为运行时未就绪时的降级。

### 2.5 右键菜单

- 在 WebView2 原生 `ContextMenuRequested` 菜单上追加应用动作（不覆盖原生复制/粘贴/检查项）：
  发送本页到对话、引用选中文字到对话、标注页面区域、在默认浏览器打开、复制地址。

### 2.6 收藏、历史与地址栏候选

- 新增 `BrowserBookmarkStore` / `BrowserHistoryStore`：原子 JSON 落盘，收藏最多 500 条、历史最多 2000 条，同一数据根复用实例。
- 真实导航成功后才记录历史；标题晚到会补全历史与收藏标题。
- 地址栏下拉用 Avalonia `Popup` 浮在 WebView2 原生子窗口之上，收藏优先、历史随后；支持上下键、Enter、Esc。

### 2.7 标注回传对话

- 工具栏“标注页面区域…”或右键菜单进入标注模式：页面内拖框，CDP `Page.captureScreenshot` 截取框选区域，弹出说明窗口。
- 结果通过 `BrowserChatReference` 进入 `MainWindow.AcceptBrowserReferenceAsync`：截图作为聊天附件，文字说明插入当前主聊天草稿；已有草稿时不覆盖，按既有“待恢复文本”语义处理。

### 2.8 智能体审批与工具

- `WebView2BrowserUseBridge` 新增 `ApprovalHandler`：navigate/click/type/evaluate/screenshot 在每次执行前按站点权限判定；`allow` 放行，`block` 直接拒绝，`approval` 等待现有 `ExtensionUiDialogWindow` 确认卡片（60 秒超时按拒绝）。
- 截图策略 `always/ask/never` 真实生效；截图不再要求“完整 CDP 权限”。
- 新增 `browser_type` 工具（Core 契约 + App 桥 + 插件注册）。
- `browser_get_state` 现在也受 Agent 控制总开关约束。

### 2.9 全宽查看与扩展

- 浏览器“全宽查看”按钮：右栏临时横跨工作区（保留左侧导航），不移动页面控件，WebView2 controller 不重建；退出恢复原布局。
- `BrowserEnvironmentOptions.AreBrowserExtensionsEnabled = true`；新增 `BrowserExtensionLoader`，从 `<dataRoot>/webview-extensions/<name>/manifest.json` 加载未打包扩展，设置页显示真实目录。
- 宽/窄工具栏自适应：窄于 380 DIP 时隐藏次要按钮，功能仍可从“更多”菜单进入。

## 3. 验证

| 命令 | 结果 |
| --- | --- |
| `dotnet build src/Fufucode.App/Fufucode.App.csproj -c Debug` | 0 错误；4 条警告均为既有 `ToolOutputView`/`WorkspacePanelView` 警告 |
| `dotnet run --project tests/Fufucode.App.Browser.Tests` | 全部通过：146 项断言（基线 138，新增 8 项） |
| `dotnet run --project src/Fufucode.App -- --native-module-selftest` | 最终一轮 `Native module failures: 0`；真实下载写文件、进度 Completed、下载历史落盘、弹窗新页签均通过。首轮曾偶发一条 “Both tabs render their own real DOM” 时序失败，复跑通过 |
| `dotnet run --project src/Fufucode.App -- --selftest --browser-integration-only` | `Browser integration failures: 0` |
| 全量 `--selftest` | 未完成：运行到 `[回车发送]` 第二条输入断言时阻塞在 Avalonia headless `KeyTextInput`，与浏览器代码无直接调用关系；本记录不把它记为通过 |

## 3.1 第二轮：三层浏览器能力补齐（2026-10-19）

用户明确三层目标：应用内浏览器、应用内浏览器控制、设备自带浏览器控制插件。

### 第三层：设备自带浏览器控制（原先近乎空白）

- 新增 `App/Features/ExternalBrowser/ExternalBrowserBridgeServer.cs`：独立本机回环 HTTP 服务，不依赖 `service.remote-control`。
  - `/api/status`、`/api/pair`（4 位 PIN、5 次锁定、常量时间比较）、`/api/next`（25 秒长轮询）、`/api/result`、`/api/extension/collect`。
  - 端口默认 `54321`，被占用时自动退回系统分配；实际端口写进设置页与扩展 `config.json`。
  - PIN/令牌按设置落盘，重启无需重新配对。
- 扩展重写：`background/service-worker.js` 增加长轮询、命令执行、结果回传、`alarms` 保活、自动读取 `config.json`、自动配对；新增 `content/agent.js`（click/type/hover/scroll/key/select/wait/get_dom）；manifest 增加 `tabs`、`alarms`、`<all_urls>`。
- 新增 `ExternalBrowserExtensionInstaller`：把扩展复制到 `<dataRoot>/external-browser-extension` 并写入端口/PIN；设置页提供“打开扩展目录”“复制配对信息”。
- 新增工具 `external_browser_state`、`external_browser_action`（14 个动作），复用站点权限与审批。
- 设置新增：外部浏览器启用、端口、PIN、连接状态、扩展目录。

### 第二层：应用内浏览器控制

- 新增工具：`browser_scroll`、`browser_hover`、`browser_key`、`browser_select`、`browser_wait`、`browser_get_dom`。
- 敏感操作语义识别：点击提交/购买/支付/删除类元素、按 Enter、敏感站点动作都会强制逐次确认。
- `browser_cdp` 改为每次使用前显式确认，对标 Codex 的 “explicit approval before full CDP”。
- 截图结果通过 Markdown 图片在用户消息气泡中可见。

### 第一层：应用内浏览器

- 标注：单击元素即选中元素区域，拖动选择区域；说明窗口新增 Adjust（字号、行高、文字色、背景色、内边距、圆角）实时预览与还原。
- 新增完整浏览历史面板：搜索、重开、逐条删除。
- 页签 favicon、音频播放指示与静音；页签拖动排序。
- 新增快捷键 `Ctrl+L`（聚焦地址栏）、`Ctrl+Shift+B`（全宽切换）。
- 设置新增表单自动填充、密码自动保存开关，接入 WebView2 profile。

### 第二轮验证

| 命令 | 结果 |
| --- | --- |
| `dotnet build src/Fufucode.App/Fufucode.App.csproj` | 0 错误 |
| `dotnet run --project tests/Fufucode.App.Browser.Tests` | 160 项断言全部通过（新增 14 项外部桥协议测试） |
| `--native-module-selftest` | `Native module failures: 0` |
| `--selftest --browser-integration-only` | `Browser integration failures: 0` |
| `node --check`（扩展全部 JS） | 通过 |

## 4. 未执行与未验证

- 全量 `--selftest` 未跑完：在 `TestEnterKeyAsync` 的第二次 `KeyTextInput` 处阻塞；已确认 mock 回合正常结束并置 Idle，阻塞发生在 Avalonia headless 输入模拟，不在浏览器路径。未据此宣称全量自测通过。
- 真实 Chrome/Edge 扩展加载与端到端动作未人工验收：需要用户在 `chrome://extensions` 加载 `<dataRoot>/external-browser-extension`，再跑一次真实点击/截图。
- 权限确认窗口、下载保存对话框、标注说明窗口的指针/IME/DPI 真实交互未逐项人工验收。
- 标注 Adjust 只覆盖字号/行高/颜色/内边距/圆角，不是 Codex 的完整设计面板。
- 未实现：内嵌 DevTools 面板（仍是外部窗口）、扩展安装/商店管理 UI、Chrome profile 导入、书签栏、云浏览器。
