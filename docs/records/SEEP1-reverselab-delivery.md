# SEEP-1 交付记录：Seep 逆向工程工作台插件

日期：2026-10-16。任务：`docs/tasks/seep-reverselab-plugin.md`（SEEP-1）。
工作树：`C:/Projects/Fucode/worktrees/seep1-reverselab-plugin`，分支 `agent/seep1-plugin`。
起点 HEAD：`7a9558e70f7c446c830756f558e23b22de7942ad`。
功能提交：`cc0e469`（本文件与该提交之后的仅文档提交构成完整交付；
分支 `agent/seep1-plugin` 的 tip 即交付 HEAD，精确 SHA 由交接卡给出，避免文件自重引用）。
状态：`module_ready`（模块待主控接线；未整合 main、未做产品验收）。

## 交付范围

把外部 Seep Reverse Lab 工作台（MCP 工具链 + 作战协议人格 + 合规转译中继）
作为**一个可在插件市场自主安装/启用/停用/卸载的产品插件**接入 Fufucode。

架构采用主控裁定的**方案 A：网络中继 + 统一 C# Stdio MCP 桥**：
MCP 工具链必须用 Python 运行；compliance-relay 为纯 Python 标准库实现且下沉到传输层，
协议无关、跨全部五个后端，零 C# 规则重写，且与 MCP 进程同源。

## 改动清单

### Core：插件目录与能力

| 文件 | 改动 |
|---|---|
| `src/Fufucode.Core/Plugins/PluginMarketplace.cs` | 新增 `tool.seep-reverse-lab`（`ToolExtension`，`DefaultInstalled: false`、`DefaultEnabled: false`）；`PluginDefinition` 新增 `public string? SystemPromptFragment { get; init; }`；新增 `PluginStateNotifier` 跨实例广播；状态写入统一收口到 `WriteState` 并新增 `Reload()` |
| `src/Fufucode.Core/Plugins/PluginCapability.cs` | 新增能力 `PluginCapability.ReverseLab`；映射 Provider → `tool.seep-reverse-lab`；短名 `Seep 逆向工程工作台` |
| `src/Fufucode.Core/Plugins/PluginToolRegistry.cs` | 新增 `Refresh()`（动态工具集重建）；`IPluginToolHandler` 新增 `OwnsToolName` / `ExplainUnavailable` / `Capability`；未注册但被处理器认领的工具名给出真实原因而不是泛化失败 |

### Core：ReverseLab 插件实现（新增目录 `src/Fufucode.Core/Plugins/ReverseLab/`）

| 文件 | 职责 |
|---|---|
| `SeepReverseLabAssets.cs` | 工作台资产解析（`SEEP_REVERSE_LAB_ROOT` / `SEEP_PYTHON` 覆盖）、人格内嵌资源加载、真实文件系统探测、进程启动参数、`ISeepProcess` / `ISeepProcessFactory` 接缝与真实进程实现 |
| `SeepMcpClientBridge.cs` | 实现 `IPluginToolHandler`：stdio JSON-RPC 2.0（`initialize` → `notifications/initialized` → `tools/list` → `tools/call`）；**工具清单完全动态**取自服务端，不硬编码；`StopAsync` 终止子进程并清空注册（幽灵工具防线） |
| `PluginSystemPromptComposer.cs` | `IPluginSystemPromptProvider` / `PluginStateSystemPromptProvider`；只拼装"已安装且已启用"插件的人格片段，未启用时不注入 |
| `ReverseLabRelayHost.cs` | 管理 `compliance-relay/relay.py` 启停；`Stopped` / `Running` / `PortOccupiedExternally` / `NotDeployed` / `Failed` 五态；外部占用时只报告不接管；判定 Running 前真实 TCP 探测监听 |
| `SeepReverseLabPluginHost.cs` | 生命周期协调器：插件市场状态 → 启动/停止子进程、刷新/清空工具表、联动中继；订阅跨实例广播 + `Reload` 保证设置页写入即时生效 |
| `Assets/seep-combat-protocol.md` | 作战协议人格原文逐字副本（`~/.pi/agent/AGENTS.md`，9020 字节，SHA256 `18571600DD3774FE6F0A8BAD541609068388E7C41F9BCA5DB55728152FAAA946`） |

### Core：会话人格注入与工具声明

| 文件 | 改动 |
|---|---|
| `src/Fufucode.Core/Backend/NativeCSharpChatSession.cs` | 每次构建请求前读取人格提供方；启用插件时在最前追加 System 消息；每轮重新计算可声明工具（内置 4 个 + 当前门禁放行的插件工具） |
| `src/Fufucode.Core/Fufucode.Core.csproj` | 以 `EmbeddedResource` 内嵌人格原文，不依赖外部路径 |

### App：市场页、门禁与组合根

| 文件 | 改动 |
|---|---|
| `src/Fufucode.App/Features/Plugins/PluginAccess.cs` | 新增 `ReverseLabPluginId`、`ReverseLabEnabled`、`ReverseLabStatus` |
| `src/Fufucode.App/ViewModels/SettingsPageViewModel.Features.cs` | 新增 `IsReverseLabInstalled` / `ReverseLabEnabled` / `ReverseLabStatusText` 及其变更通知 |
| `src/Fufucode.App/Views/SettingsPage.axaml` | 能力解锁状态区新增 Seep 状态行（`marketplace-reverse-lab-status`） |
| `src/Fufucode.App/Views/MainWindow.ReverseLab.cs`（新增） | 组合根接线：独立插件状态存储 + 独立工具注册表 + 生命周期宿主 + 人格提供方；关窗时终止子进程 |
| `src/Fufucode.App/Views/MainWindow.axaml.cs` | 构造期调用 `InitializeReverseLabPlugin()`；`OnClosing` 调用 `ShutdownReverseLabAsync()` |
| `src/Fufucode.App/Integration/NativeSessionBootstrap.cs` | 新增可选 `systemPromptProvider`；传入 `PluginToolRegistry` 时同一实例既作调度器也作声明来源 |

### 测试

| 文件 | 覆盖 |
|---|---|
| `tests/Fufucode.App.Backend.Tests/SeepReverseLabPluginTests.cs`（新增） | 目录/能力映射、人格资源、23 工具动态映射与参数 Schema、未安装/停用/卸载门禁、幽灵工具负向断言、跨实例状态同步、人格注入（含真实会话请求）、中继五态、工作台未部署 |
| `tests/Fufucode.App.Backend.Tests/SeepFixtures.cs`（新增） | 脚本化 MCP 子进程替身（回放真实录制清单，真实读请求行并回标准 JSON-RPC）；真实回环端口占用/探测；中继替身工厂 |
| `tests/Fufucode.App.Backend.Tests/Fixtures/seep-tools-list.json`（新增） | 2026-10-16 从真实 `seep_mcp_server.py` 录制的 `tools/list` 原文（23 项，30090 字节） |
| `tests/Fufucode.App.Backend.Tests/SeepRealMcpProbe.cs`（新增） | 可选真实探针 `--seep-live`：真实启动 MCP 子进程、核对 23 工具、真实调用、真实卸载与 PID 退出核验 |
| `tests/Fufucode.App.Backend.Tests/SeepRealRelayProbe.cs`（新增） | 可选真实探针 `--seep-relay-live`：沙箱复制中继目录后真实启动 relay.py 并核验启停 |
| `tests/Fufucode.App.Settings.Tests/PluginGatingTests.cs` | 追加市场页 Seep 条目投影、安装/停用/卸载文案与开关状态断言 |

## 验证证据（全部本机实跑）

| 验证 | 命令 | 结果 |
|---|---|---|
| 全量构建 | `dotnet build Fufucode.sln` | **0 警告 0 错误** |
| 插件与内核定向测试 | `dotnet run --project tests/Fufucode.App.Backend.Tests` | **573 项断言全部通过**（含 SEEP-1 新增段） |
| 设置页门禁测试 | `dotnet run --project tests/Fufucode.App.Settings.Tests` | **84 项断言全部通过** |
| 本地插件模块测试 | `dotnet run --project tests/Fufucode.App.Plugins.Tests` | PASS（166 + 21 项） |
| 应用无头自测 | `dotnet run --project src/Fufucode.App -- --selftest` | **0 项失败** |
| 真实 MCP 联调 | `dotnet run --project tests/Fufucode.App.Backend.Tests -- --seep-live` | **ALL PASS**：真实子进程 PID 55236，`serverInfo=seep 1.28.1`，`toolCount=23`，真实 `seep_status` 返回 radare2 5.9.8 / JADX / Apktool 就绪，卸载后 PID 已退出 |
| 真实中继联调 | `dotnet run --project tests/Fufucode.App.Backend.Tests -- --seep-relay-live` | **ALL PASS**：真实启动 relay.py 进入监听，停止后端口释放，不留常驻进程 |

真实联调实测到的事实（写入记录以免后续误判）：
`seep MCP` 的工具级错误以**文本**形式返回（例：`[ERROR]: Target file does not exist: ...`），
**未设置** MCP `isError` 标志。桥的契约是忠实转述 `result` / `isError`；
把文本启发式判成失败属越权推测，因此桥不改写服务端语义，真实探针只断言"原文被完整转述"。

## 未执行 / 未验证

- **未做真实 GUI 交互验证**：未在真实桌面会话中手动点击插件市场安装/启停按钮并观察界面。
  已验证的是无头自测（0 失败）、App 层组合根接线与设置页 VM 的真实状态投影测试。
- **未验证 23 个工具中的重工具**：`seep_r2_*` 的完整分析链、`seep_apk_*` 与 JADX/Apktool
  实跑仅在 `seep_status` 的就绪度探测层面确认；未对真实样本做端到端反编译验证。
- **未启用中继联动作为默认路径**：`SeepReverseLabPluginHost` 默认 `RelayEnabled: true`，
  但组合根 `MainWindow.ReverseLab.cs` 以默认值构造；真实联调探针在隔离端口验证，
  未验证 `127.0.0.1:17890` 与真实模型请求 `baseUrl` 的接入改造（该改造属共享组合根，
  需主控确认后再动）。
- **未修改** `C:\Tool\Reverse\Seep-Reverse-Lab` 下任何工作台源文件（只读使用）。
- **未做产品验收**：本卡为模块待接线状态，共享 shell 的最终整合与产品验收由主控 M 负责。

## 公共接线需求（交主控）

1. **中继接入模型请求**：当前中继宿主已能真实启停 `127.0.0.1:17890`，
   但"把会话模型请求 `baseUrl` 指向中继"尚未接线（属共享组合根/设置改动）。
   需要主控决定接入点与是否默认启用。
2. **会话创建点接线**：`NativeSessionBootstrap.CreateNativeSession` 已支持
   `systemPromptProvider` 与插件注册表参数；主控需在实际创建原生会话处传入
   `SystemPromptProviderForTesting`（即 `_reverseLabPromptProvider`）与
   `_reverseLabToolRegistry`，插件工具与人格才会进入真实会话。
   当前组合根已把两者建好并暴露为 internal 访问器，但未在会话创建路径传入。
3. **非必需插件默认策略**：`tool.seep-reverse-lab` 按纪律 `DefaultInstalled: false` /
   `DefaultEnabled: false`；未安装时不注册工具、不启动子进程、不渲染可用开关。

## 临时状态

- 无临时权限、设备或进程需要回收。
- 交付前已确认本任务启动的 MCP/中继子进程全部退出（真实探针断言 PID 已退出、端口已释放）。
- 系统中另有两个**早于本任务**已运行的 `seep_mcp_server.py` 进程
  （PID 30904 起于 2026-10-15 22:32，PID 52608 起于 23:45，父进程均为 `node.exe`），
  属用户自己的工具链，**未触碰、未终止**。

## 机器交接卡

```json
{
  "schemaVersion": 1,
  "type": "delivery.card",
  "taskId": "SEEP-1",
  "status": "module_ready",
  "summary": "Seep 逆向工作台已作为可选插件接入（目录/能力/MCP 桥/人格/中继/市场 UI），默认未安装；共享接线与产品验收待主控",
  "worktree": "C:/Projects/Fucode/worktrees/seep1-reverselab-plugin",
  "branch": "agent/seep1-plugin",
  "base": "7a9558e70f7c446c830756f558e23b22de7942ad",
  "head": "cc0e4697efee2418b63f727ff21d71eb01da6bd3",
  "headRule": "head 为承载全部源码与测试改动的功能提交（已用 git show --stat 核对该提交包含 26 个文件的全量改动）。本卡自身及此后的仅文档提交不改变工作树内容，故分支 agent/seep1-plugin 的 tip 可能比 head 多出纯文档提交；权威 tip 由本次交接消息给出，且必为 head 的后代。",
  "commits": ["cc0e4697efee2418b63f727ff21d71eb01da6bd3"],
  "changedFiles": [
    "src/Fufucode.Core/Plugins/PluginMarketplace.cs",
    "src/Fufucode.Core/Plugins/PluginCapability.cs",
    "src/Fufucode.Core/Plugins/PluginToolRegistry.cs",
    "src/Fufucode.Core/Plugins/ReverseLab/SeepReverseLabAssets.cs",
    "src/Fufucode.Core/Plugins/ReverseLab/SeepMcpClientBridge.cs",
    "src/Fufucode.Core/Plugins/ReverseLab/ReverseLabRelayHost.cs",
    "src/Fufucode.Core/Plugins/ReverseLab/PluginSystemPromptComposer.cs",
    "src/Fufucode.Core/Plugins/ReverseLab/SeepReverseLabPluginHost.cs",
    "src/Fufucode.Core/Plugins/ReverseLab/Assets/seep-combat-protocol.md",
    "src/Fufucode.Core/Backend/NativeCSharpChatSession.cs",
    "src/Fufucode.Core/Fufucode.Core.csproj",
    "src/Fufucode.App/Features/Plugins/PluginAccess.cs",
    "src/Fufucode.App/ViewModels/SettingsPageViewModel.Features.cs",
    "src/Fufucode.App/Views/SettingsPage.axaml",
    "src/Fufucode.App/Views/MainWindow.ReverseLab.cs",
    "src/Fufucode.App/Views/MainWindow.axaml.cs",
    "src/Fufucode.App/Integration/NativeSessionBootstrap.cs",
    "tests/Fufucode.App.Backend.Tests/SeepReverseLabPluginTests.cs",
    "tests/Fufucode.App.Backend.Tests/SeepFixtures.cs",
    "tests/Fufucode.App.Backend.Tests/SeepRealMcpProbe.cs",
    "tests/Fufucode.App.Backend.Tests/SeepRealRelayProbe.cs",
    "tests/Fufucode.App.Backend.Tests/Fixtures/seep-tools-list.json",
    "tests/Fufucode.App.Backend.Tests/Fufucode.App.Backend.Tests.csproj",
    "tests/Fufucode.App.Backend.Tests/Program.cs",
    "tests/Fufucode.App.Settings.Tests/PluginGatingTests.cs",
    "docs/records/SEEP1-reverselab-delivery.md"
  ],
  "verification": {
    "passed": [
      "dotnet build Fufucode.sln -> 0 warnings 0 errors",
      "tests/Fufucode.App.Backend.Tests -> 573 assertions passed (includes new SEEP-1 section)",
      "tests/Fufucode.App.Settings.Tests -> 84 assertions passed (includes Seep marketplace/gating)",
      "tests/Fufucode.App.Plugins.Tests -> ALL PASS (166 + 21)",
      "src/Fufucode.App --selftest -> 0 failures",
      "--seep-live -> ALL PASS: real python child pid 55236, serverInfo 'seep 1.28.1', toolCount 23, real seep_status ready, uninstall kills pid",
      "--seep-relay-live -> ALL PASS: real relay.py listening then released"
    ],
    "failed": [],
    "notRun": [
      "GUI manual click-through of marketplace install/enable toggles on a real desktop session",
      "End-to-end heavy tool runs (seep_r2_* full analysis chain, seep_apk_* with JADX/Apktool) on real samples",
      "127.0.0.1:17890 relay wired into real model request baseUrl (shared composition root)",
      "tests/Fufucode.Core.Tests real-Pi segment: blocked by pre-existing missing runtime/node/node.exe in this worktree, unrelated to SEEP-1"
    ]
  },
  "evidence": [
    "docs/records/SEEP1-reverselab-delivery.md",
    "tests/Fufucode.App.Backend.Tests/Fixtures/seep-tools-list.json (recorded real tools/list, 23 tools, 30090 bytes)",
    "persona asset sha256 18571600DD3774FE6F0A8BAD541609068388E7C41F9BCA5DB55728152FAAA946 (byte-identical to ~/.pi/agent/AGENTS.md, 9020 bytes)"
  ],
  "integrationNeeds": [
    "Pass _reverseLabPromptProvider and _reverseLabToolRegistry into the real native-session creation path (NativeSessionBootstrap.CreateNativeSession already accepts them; composition root must supply them).",
    "Decide and implement pointing model request baseUrl at 127.0.0.1:17890 compliance relay (shared composition root / settings change).",
    "Main control owns final market UI wiring review and product acceptance."
  ],
  "blockers": [],
  "writerStopped": true
}
```
