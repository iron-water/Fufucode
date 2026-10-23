# Computer Use 后端调研与实现边界

日期：2026-10-22。状态：current research / implementation baseline。

## 结论

1. **Sky 不能作为本项目的默认后台保证。** 本机 OpenAI Computer Use 插件公开的 `@oai/sky` window2 API 提供 `list_apps`、`get_window_state`、`click`、`press_key`、`type_text`、`scroll`、`set_value`、`drag` 等按 `Window` 对象操作的方法，但 API 参考没有 `delivery_mode` 或后台投递参数；`activate_window` 明确是把窗口带到前台的逃生入口。现阶段 Fufucode 不把 Sky 的窗口对象误报成“独立后台输入”。
2. **CUA Driver 是 Windows 后台投递的优先后端。** 官方 Windows 文档定义 `delivery_mode: "background"` 为默认严格后台模式：优先 UI Automation Invoke，必要时使用窗口消息；不 raise、不 restack、不移动真实鼠标。目标被遮挡、应用已知丢弃该事件类型或没有可用 UIA 路径时，返回 `background_unavailable`，由调用方决定是否显式升级到 `foreground`。Fufucode 保留这个错误，不自动升级。
3. **沙箱/VM 不是所有后台操控的前置条件。** 如果目标是 Win32/WPF/WinUI/UWP 等且 CUA Driver 能通过 UIA/窗口消息投递，可以在当前交互会话里后台操作。若目标是 Electron/Chromium 自绘区域、终端、游戏、Canvas、被遮挡且无 UIA 的表面，后台路由可能不可用；需要前台时用显式 `foreground`，要保护用户主桌面则使用独立 Windows VM/桌面会话。OpenAI 官方 Computer Use 文档把独立 VM/隔离桌面作为原生桌面控制的推荐隔离手段。
4. **“自己单独一个鼠标”应理解为窗口级事件通道，而不是第二个 OS 光标。** CUA Driver 的后台路径不移动用户真实光标；窗口目标通过 UIA/窗口消息接收事件。它不能保证所有应用拥有一个可见、独立、与用户鼠标并行的硬件光标。Fufucode 的 Win32 `SendInput` 只保留给显式前台模式，因此不会被包装成独立后台鼠标。
5. **多线程可以安全并发调用，但 GUI 输入必须串行。** CUA Driver 文档明确：多个 session 仍共享屏幕、键盘、焦点，原生输入一次只允许一个动作。Fufucode 增加全局 `input` 操作闸门，防止多个线程把按键/鼠标序列交错；截图按 `processId + windowId` 分资源锁，不同窗口可以并行观察。需要真正同时操控且完全隔离的多个 GUI 工作单元，应为每个 worker 使用独立 VM/桌面会话或改用浏览器/CDP、应用 API 等无焦点接口。

## 已实现

- `src/Fufucode.Core/Plugins/ComputerUseContracts.cs`
  - 增加 `ComputerUseDeliveryMode`、`ComputerUseTarget`、后端能力声明和统一结果错误码。
  - 增加 `ComputerUseOperationGate`：输入全局串行、截图按窗口资源隔离。
- `src/Fufucode.Plugins.ComputerUse/ComputerUseBackends.cs`
  - `CuaDriverComputerUseBackend`：调用 `cua-driver call`，传递窗口目标与 `delivery_mode`，解析 `background_unavailable`，不自动前台化。
  - `SkyComputerUseBackend`：只接受宿主注入的委托，不猜测 Sky 私有 RPC；公开 API 默认标记为前台能力，宿主必须显式证明后台能力后设置 `SupportsBackground=true`。
  - `Win32ComputerUseBackend`：保留旧实现，但只接受显式 `foreground`。
- `src/Fufucode.Plugins.ComputerUse/ComputerUsePlugin.cs`
  - 工具参数增加 `deliveryMode`、`processId`、`windowId`、`elementToken`、`session`。
  - 默认后台动作没有窗口目标时直接拒绝，防止误触用户当前焦点。
  - 增加 `computer_capabilities` 工具，报告真实后端能力。
- `src/Fufucode.Core/Plugins/PluginMarketplace.cs` 与 `manifest.json`
  - Computer Use 升级到 1.1.0，市场文案明确后台投递与前台显式升级。

## 2026-10-23 混合路由、虚拟光标与并发分桶

1. **混合路由（`route` 参数）**：
   - `auto` + `background`：优先 CUA Driver 的严格窗口投递；Sky 契约上不支持后台，因此不冒充。
   - `auto` + `foreground` / `visible`：优先 Sky 提高成功率，再 CUA，最后 Win32 前台兼容。
   - 显式 `route=sky|cua|win32` 是固定路由，失败时不静默换栈；上层可读到真实错误后再决定。
2. **虚拟光标 Overlay**：
   - `ComputerUseVirtualCursorRegistry` 只保存 Agent 光标状态（坐标、颜色、按压态、目标窗口），不调用 `SendInput`、UIA 或任何输入 API。
   - `ComputerCursorOverlayWindow` 是透明、置顶、`WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE` 的点击穿透层，铺满主屏，按 Agent 绘制箭头与名称胶囊。
   - 首个光标出现时才创建，全部移除或窗口关闭时销毁；无头自测不创建真实窗口。
   - 插件参数 `agentId` 存在且鼠标动作成功时更新光标；`screenX/screenY` 可给出全局屏幕坐标，缺省回退到窗口相对 `x/y`。
   - 这实现的是“可见的多个虚拟光标”，不是第二个 Windows 系统指针；目标应用内部感知的指针位置仍由 UIA/窗口消息或前台输入决定。
3. **并发分桶**：
   - 后台输入锁键为 `input:window:{pid}:{wid}`，不同目标窗口可以并行；前台输入锁为 `input:foreground`，仍然全局串行，因为前台激活共享用户桌面焦点。
   - 截图按目标窗口资源加锁，与输入锁分离。
   - 真正完全隔离的多桌面并发仍应使用独立 VM / RDP Session；普通桌面上的多个 Overlay 光标不会隔离焦点与输入队列。

## 未验证项

- 本机没有发现 `cua-driver` 可执行文件，因此未运行真实后台 UIA/PostMessage 投递。
- 当前会话没有可用的 `node_repl`/Sky 宿主桥，未对 Sky 真实窗口执行输入。
- 未对 Electron/Chromium、终端、Canvas、游戏和多显示器环境做实机覆盖。
- 现有定向测试使用替身后端和旧 Win32 替身；真实桌面验收仍需在 CUA Driver 已安装且交互式 Session 运行时执行。
- 虚拟光标 Overlay 只验证了状态同步与构建，未在真实多显示器/125% DPI/多 Agent 同时动作下做实机截图核对。
- 后台按窗口分桶已由代码与闸门测试覆盖，但未用两个真实窗口并发执行 CUA 动作做端到端压力测试。

## 外部依据

- OpenAI Computer Use：<https://developers.openai.com/api/docs/guides/tools-computer-use>
- OpenAI Sky 插件 API（本机缓存版本 26.1013.31256）：`C:\Users\mio\.codex\plugins\cache\openai-bundled\computer-use\26.1013.31256\docs\api.md`
- CUA Driver Windows 后台投递：<https://github.com/trycua/cua/blob/main/libs/cua-driver/rust/Skills/cua-driver/WINDOWS.md>
- CUA Driver action ladder / multi-client 语义：<https://github.com/trycua/cua/blob/main/docs/content/docs/cua-driver/concepts/how-cua-driver-works.mdx>
- CUA Driver click/type 参数：<https://github.com/trycua/cua/blob/main/docs/content/docs/cua-driver/reference/mcp-tools/click-and-type.mdx>
