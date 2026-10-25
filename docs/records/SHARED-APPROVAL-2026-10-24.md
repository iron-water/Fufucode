# 审批单一 gate（2026-10-24）

状态：实现完成，构建与测试通过；未做真实三方并发联调。
工作路径：`C:\Projects\Fucode\Fufucode`，分支 `main`，未提交。

## 问题

在此之前的审批有两条互不知情的路径：

- FUFU 连接插件直接订阅 `IExtensionUiSession.ExtensionUiRequestReceived`，自行调用 `RespondToExtensionUiAsync`；
- Jiii 连接插件通过 `SessionApprovalGate` 另建一套订阅。

同一个工具审批可能被两边分别应答；后到者还会拿到一个看似成功的回执。

## 方案

真实审批真源仍是 `IExtensionUiSession`，但两个插件不再各自订阅它，而是共享同一个 gate。

```text
IExtensionUiSession
        │
        ▼
  SharedApprovalGate            （每会话一个，先到先得）
   ├─ Requested / Decided
   ├─ DecideAsync(id, approved, source)
   └─ Pending（读真实会话）
        │
        ├── FufuApprovalBridge   → FUFU 管道
        └── SharedGateApprovalAdapter → 远控 / Jiii
```

### 新增

| 文件 | 内容 |
|---|---|
| `src/Fufucode.Core/Approvals/SharedApprovalGate.cs` | 单一 gate：按会话共享、先到先得、会话关闭如实失效、`WaitAsync`、有界决定记录 |
| `src/Fufucode.Plugins.RemoteControl/SharedGateApprovalAdapter.cs` | 把共享 gate 适配为远控 `IRemoteToolApprovalGate` |
| `src/Fufucode.Plugins.FufuBridge/FufuApprovalBridge.cs` | 把共享 gate 适配为 FUFU 管道审批入口 |
| `tests/Fufucode.App.RemoteControl.Tests/SharedApprovalTests.cs` | 10 项回归断言 |

`SharedApprovalRegistry` 用 `ConditionalWeakTable` 按会话对象共享 gate，会话释放后自动回收。

### 行为

- **先到先得**：第一个 `DecideAsync` 生效；第二个返回 `Accepted=false`，并说明「该审批已由 jiii / fufu 处理」。
- **不重复回传**：后到者不会触发第二次 `RespondToExtensionUiAsync`。
- **原始请求**：回传的是真实会话的 `ExtensionUiRequest` 对象与 generation，不重建。
- **关闭即失效**：未决请求在会话关闭时标记失效，并通知两个入口。
- **不覆盖成功**：已决定的请求不会被随后的关闭事件改写成失效。
- **未知请求**：不伪造批准，直接返回失效。

### 修改

| 文件 | 改动 |
|---|---|
| `RemoteControlHost.cs` | `Attach` 时用 `SharedApprovalRegistry.For(ui)` 创建审批入口；新增 `CreateApprovals` |
| `RemoteSessionBridge.cs` | `Rebind` 支持传入审批入口，避免切换会话时重建第二套 |
| `FufuBridgeHost.cs` | 改为订阅 `FufuApprovalBridge`；`approve` 走共享 gate；关闭时解绑 |
| `RemoteControlPluginPackage.cs` / `FufuBridgePackage.cs` | 与上一轮的 connector 注册共用同一批对象 |

### 线协议兼容

FUFU 管道的 `requestId` 字段值改为 gate 的 `ApprovalId`（`generation:id`）。
FUFU 侧按不透明字符串回传即可，不需要改协议结构；但**已发送的旧 `requestId` 无法再匹配**，重连后由快照补齐。

## 实际验证

- `dotnet build Fufucode.sln -m:1 -p:BaseOutputPath=obj/solution-verify/`：52 个项目，0 错误。
  - 6 条警告均为既有项：`ChatPage.Composer.cs` 的 CS8602、FUFU 的 5 条 AVLN3001。
  - 直接构建时若主输出目录被运行中的 `Fufucode.App.exe` 锁定，会出现 MSB3027；改用隔离输出目录验证。
- `dotnet run --project tests/Fufucode.App.RemoteControl.Tests`：**154 项断言全过**（原 139 + 新增 15）。
- `dotnet run --project tests/Fufucode.Fufu.Tests`：全部通过。
- 新增断言覆盖：gate 复用、单次广播、先到先得、后到者被拒且不重复 Respond、未知请求、关闭失效、成功不被关闭覆盖、原始请求回传、不同会话隔离。

### 测试抓到的真实缺陷

第一轮测试失败于「后到者收到已由 jiii 处理的说明」。

原因：真实会话在 `RespondToExtensionUiAsync` 内部**同步触发** `ExtensionUiRequestClosed`，此时 gate 还没把成功决定写进 `_decided`，关闭事件先把该请求记成了失效。

修复：在回传前先把 `ApprovalId` 标记为 `_deciding`，`OnClosed` 遇到正在决定的请求直接跳过。这是一个只有在真实会话同步回调下才会暴露的竞态。

## 未完成 / 边界

- 没有三方并发联调：测试用同步假会话，真实 Provider 的异步回调时序未验证。
- 桌面 UI 自己的审批按钮仍未接入共享 gate；目前是 FUFU 与 Jiii 两条路统一，桌面本地审批属于第三条路。
- `WaitAsync` 采用 50ms 轮询，长时间等待会占用线程；生产环境可换成 `TaskCompletionSource`。
- 未提交 Git。

## 下一步建议

1. 把桌面 UI 审批也接到 `SharedApprovalGate`，彻底消除第三条路径。
2. 用真实 Provider 跑一次「手机先批 / 宠物后到」的并发验证。
