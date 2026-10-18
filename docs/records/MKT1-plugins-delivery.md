# MKT-1 插件能力动态装卸与权限门禁交付记录

日期：2026-10-15。任务：MKT-1。工作树：`C:/Projects/Fucode/worktrees/mkt1-plugins`，分支 `agent/mkt1-plugins`。
起点 SHA：`dcb7415`（与 `main` 的 merge-base 一致）。难度：L3（多文件、原生行为、能力门禁与生命周期接线）。

## 任务目标与实现

### 1. 插件能力门禁层（Core，新增）

| 文件 | 作用 |
| --- | --- |
| `src/Fufucode.Core/Plugins/PluginCapability.cs` | 能力枚举、能力→插件映射（唯一真源）、能力状态快照、`PluginCapabilityGateway` 门禁 |
| `src/Fufucode.Core/Plugins/PluginToolRegistry.cs` | 插件工具调度表：按实时插件状态动态注册/注销，未安装不暴露、卸载后失效 |
| `src/Fufucode.Core/Plugins/BrowserUsePlugin.cs` | Browser Use 工具集（`browser_cdp` / `browser_evaluate` / `browser_navigate`）与 `BrowserUseBridge` 宿主接缝 |
| `src/Fufucode.Core/Plugins/ComputerUsePlugin.cs` | 电脑操控工具集（截图 / 鼠标 / 键盘）与宿主接缝、执行开关 |
| `src/Fufucode.Core/Plugins/ComputerUseWin32.cs` | Win32 GDI 屏幕截图 + 零依赖 PNG 编码（含 BMP 回退） |
| `src/Fufucode.Core/Plugins/ComputerUseInputWin32.cs` | Win32 `SendInput` 鼠标/键盘模拟 |
| `src/Fufucode.Core/Plugins/PluginMarketplace.cs` | 新增 `IsActive(pluginId)`（已安装且已启用） |

关键语义：

- **能力→插件映射唯一**：`PluginCapabilityRegistry.ProviderByCapability` 是“浏览器控制→agent.browser-use、电脑操控→agent.computer-use”的唯一真源；未登记能力不返回假提供者。
- **安装 ≠ 启用**：`PluginCapabilityStatus` 同时记录 `IsInstalled` 与 `IsEnabled`，两者分别驱动文案与工具暴露；只有 `IsAvailable`（已安装且已启用）才暴露工具。
- **动态装卸**：`PluginToolRegistry.DispatchAsync` 每次调用都重新查询当前插件状态；卸载/停用后立即不暴露，且再次直接调用会被拒绝，**不留可调用的幽灵工具**。
- **未接线宿主不伪造成功**：`BrowserUsePlugin.ExecuteAsync` 在 `Bridge` 为 null 时返回“尚未接线到真实浏览器宿主”，而不是编造 CDP 结果。

### 2. 浏览器控制（Browser Use）

- 设置页“开发者模式 → 启用完整 CDP 访问权限”改为真实读取插件状态：
  - 未安装时整行提示 `未安装：请在插件市场中安装 Browser Use 扩展`，并把开关**禁用**（不渲染假开关）；
  - 安装并启用后自动解锁 CDP 接口与 Browser Use 动作契约；
  - 卸载后回到未安装态、工具注销。
- 插件市场页新增“能力解锁状态”卡片，如实显示 Browser Use / Computer Use 的安装与启用状态（`marketplace-browser-use-status` / `marketplace-computer-use-status`）。
- 设置侧门禁统一走 `src/Fufucode.App/Features/Plugins/PluginAccess.cs`，避免各处自行判断插件可用性。

### 3. 电脑操控（Computer Use）

- `ComputerUsePlugin` 提供三项基础工具：`computer_screenshot`（Win32 屏幕截图，GDI `BitBlt` + 零依赖 PNG 编码，支持 `returnBase64` 与区域参数）、`computer_mouse`（移动/单击/右键/双击/滚动）、`computer_keyboard`（键入/按键/组合键，统一走 `SendInput`）。
- 未安装时**不注入任何工具**；安装并启用后方可注册进工具调度表；卸载后撤销注册。截图带真实输出目录落盘。
- 权限：插件未绕过统一审批；`ScreenCaptureEnabled` / `InputSimulationEnabled` 为执行开关，关闭时如实报告“已关闭”而非伪装成功。
- 平台边界：非 Windows 环境如实报告不支持（不创建 Win32 对象、不伪造可用性）。

## 实际验证

| 验证 | 命令 | 结果 |
| --- | --- | --- |
| 全解决方案构建 | `dotnet build Fufucode.sln` | **0 警告 · 0 错误** |
| 插件能力与门禁 | `dotnet run --project tests/Fufucode.App.Backend.Tests` | **381 项断言全部通过（0 FAIL）**，含新增 `PluginCapabilityTests` |
| 设置页门禁 | `dotnet run --project tests/Fufucode.App.Settings.Tests` | **66 项断言全部通过（0 FAIL）**，含新增 `PluginGatingTests` |

新增断言覆盖（正/负双向）：

- 未安装：能力不可用、原因含“未安装”、工具不进入调度表、直接调用被拒且原因指向未安装；
- 安装启用：三个 Browser Use 动作契约与三个电脑操控工具全部注册、带参数 Schema、CDP 调用转发到宿主桥并回显结果；
- 停用：**与未安装区分**（已安装未启用），工具从调度表撤销、调用被拒绝；
- 卸载：两能力工具全部撤销，不存在幽灵 CDP/截图工具；重复注销幂等；
- 门禁隔离：只装电脑操控不影响 Browser Use 门禁，反之亦然；未登记能力无假提供者；
- 未接线宿主：CDP 调用如实报告“尚未接线”；
- 参数校验：缺坐标的鼠标动作按参数错误拒绝；截图开关关闭时如实拒绝。

### 真实 Win32 截图独立验证（非替身）

在生产实现上实跑一次真实桌面截图（`ComputerUsePlugin(outputDirectory: ...)`，`returnBase64=false` 之外单独另跑一次）：

- 结果：`SUCCESS=True`，`屏幕截图成功：2048×1280，592157 字节`，真实落盘到输出目录。
- 结构校验：PNG 签名正确；IHDR `2048×1280`、位深 8、颜色类型 6（RGBA）；分块序列 IHDR/IDAT/IEND 合法；IDAT 用 zlib 解压后长度 `10487040 = (2048*4+1)*1280` 精确匹配，采样中 2621 个非黑像素点证明是真实屏幕内容而非全黑空帧。

## 未运行 / 未验证

- **宿主公共接线未做**：`BrowserUseBridge` 的具体实现需把插件接到 App 侧 `BrowserViewHost.CallDevToolsProtocolMethodAsync` / `ExecuteScriptAsync` 与共享动作表；按任务边界属共享组合根接线，由 M 统一完成。
- **电脑操控工具未在真实工具调度链路上端到端接线**：`PluginToolRegistry` 与 `NativeCSharpChatSession` 的工具名列表（当前硬编码 `read_file`/`write_file`/`list_dir`/`run_command`）合并属 M 的统一接线范围；本任务交付可用的门禁与工具模块。
- **未见真实用户点击式 UI 验收**：设置页门禁状态为 VM + 视图绑定层验证（含 headless 可绑定属性），未做人工目视点击验收。
- **共享区域既有失败（非本任务引入）**：`--selftest --browser-integration-only` 中 `resolved native new-tab creates an actual independent tab` 在**干净 HEAD `dcb7415`** 上同样失败（已用 stash 对照验证），与 MKT-1 无关，未在本任务修改。
- 为让设置测试反映真实目录，同步修正了两条**既有陈旧断言**（`marketplace` 条目在更早提交已进入“大功能”层级但测试未同步），属测试与真实目录对齐，未放宽任何断言。

## 公共接线需求

1. 组合根把 `PluginToolRegistry`（含 `ComputerUsePlugin`、`BrowserUsePlugin`）接入会话工具声明与调度（`NativeCSharpChatSession`/`ProductionToolDispatcher`）。
2. App 侧实现 `BrowserUseBridge`，桥接到真实 WebView2 CDP/脚本与页面导航动作。
3. 设置页能力状态卡片与 CDP 行随插件市场装卸实时刷新（当前为查询式读取 + `PluginStateStore.Changed` 订阅）。

## 状态

`submitted`（模块交付 + 待 M 公共接线）。

## 交付卡（MKT-1）

```json
{
  "schemaVersion": 1,
  "type": "delivery.card",
  "taskId": "MKT-1",
  "status": "submitted",
  "worktree": "C:/Projects/Fucode/worktrees/mkt1-plugins",
  "branch": "agent/mkt1-plugins",
  "baseSha": "dcb7415628b01e3754f0d7feeb020a876f76047a",
  "deliveryHead": "b07ffe373d67301bfb801fd46f3bae8637797189",
  "commits": ["b07ffe373d67301bfb801fd46f3bae8637797189"],
  "changedFiles": [
    "src/Fufucode.Core/Plugins/PluginCapability.cs",
    "src/Fufucode.Core/Plugins/PluginToolRegistry.cs",
    "src/Fufucode.Core/Plugins/BrowserUsePlugin.cs",
    "src/Fufucode.Core/Plugins/ComputerUsePlugin.cs",
    "src/Fufucode.Core/Plugins/ComputerUseWin32.cs",
    "src/Fufucode.Core/Plugins/ComputerUseInputWin32.cs",
    "src/Fufucode.Core/Plugins/PluginMarketplace.cs",
    "src/Fufucode.App/Features/Plugins/PluginAccess.cs",
    "src/Fufucode.App/ViewModels/SettingsPageViewModel.Features.cs",
    "src/Fufucode.App/Views/WorkbenchSectionsView.cs",
    "src/Fufucode.App/Views/SettingsPage.axaml",
    "tests/Fufucode.App.Backend.Tests/PluginCapabilityTests.cs",
    "tests/Fufucode.App.Backend.Tests/Program.cs",
    "tests/Fufucode.App.Settings.Tests/PluginGatingTests.cs",
    "tests/Fufucode.App.Settings.Tests/Program.cs",
    "docs/records/MKT1-plugins-delivery.md"
  ],
  "verification": {
    "passed": [
      "dotnet build Fufucode.sln -> 0 warnings 0 errors",
      "dotnet run --project tests/Fufucode.App.Backend.Tests -> 381 assertions PASS, 0 FAIL",
      "dotnet run --project tests/Fufucode.App.Settings.Tests -> 66 assertions PASS, 0 FAIL",
      "real Win32 screen capture -> SUCCESS 2048x1280, 592157 bytes, valid PNG (IHDR 2048x1280 bd8 ct6, IDAT inflates to (2048*4+1)*1280)"
    ],
    "failed": [],
    "notRun": [
      "App-side BrowserUseBridge wiring to real WebView2 (main-control integration scope)",
      "PluginToolRegistry merged into NativeCSharpChatSession tool list (main-control integration scope)",
      "human visual click-through acceptance of settings gating"
    ]
  },
  "evidence": [
    "docs/records/MKT1-plugins-delivery.md",
    "tests/Fufucode.App.Backend.Tests/PluginCapabilityTests.cs",
    "tests/Fufucode.App.Settings.Tests/PluginGatingTests.cs"
  ],
  "integrationNeeds": [
    "wire PluginToolRegistry + ComputerUsePlugin/BrowserUsePlugin into session tool declaration/dispatch",
    "implement BrowserUseBridge in App over WebView2 CDP/script/navigation"
  ],
  "blockers": [],
  "writerStopped": true
}
```
