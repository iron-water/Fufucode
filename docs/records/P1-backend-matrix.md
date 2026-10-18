# P1-BACKEND 交付记录：多核心后端统一契约与方案 B 矩阵

## 交付卡（执行会话，2026-10-14）

- 任务：P1-BACKEND，难度 L3，推荐模型 `group/auto-deepseek-v4-1-flash`（max 思考强度）。
- 实际工作树：`C:\Projects\Fucode\worktrees\p1-backend`，分支 `agent/p1-backend`。
- 起点 HEAD：`3c03b44b0f05395b1ebfc506e802f862f5d51ecb`。
- 起始状态：工作树干净、无未提交内容；未同步 main、未 rebase、未 push、未清理、未派下一包。
- 提交：见文末「提交与 HEAD」；以 `git rev-parse HEAD` 为准，工作树干净后停写（writerStopped: true）。
- 范围（均在允许路径内）：
  - `src/Fufucode.Core/Backend/**`（7 文件）
  - `src/Fufucode.App/Features/Backend/**`（2 文件）
  - `tests/Fufucode.App.Backend.Tests/**`（7 文件）
  - `docs/records/P1-backend-matrix.md`（本记录）
- 未触碰：`MainWindow*` 等共享外壳、`Fufucode.sln`、`AppSettings.cs`、`Fufucode.Pi`、`docs/decisions.md` 及任何权威计划/进度文件。

## 交付内容

### 1. 多核心后端统一契约（`IBackendEngine` / `IBackendRegistry`）

- `BackendEngineContract.cs`
  - `EngineId`：四种执行内核 `Pi`（默认 RPC 内核）、`Omp`（独立引擎/CLI 进程契约）、`Codex`（协议适配）、`Dsh`（DeepSeek Harness 协议适配）。
  - `BackendContractKind`：`RpcKernel` / `ExternalCliEngine` / `ProtocolAdapter`，只描述接入形态，不代表能力协商。
  - `IBackendEngine`：标准运行时探测与状态暴露——`Id`、`DisplayName`、`ContractKind`、`Runtime`。
  - `BackendRuntime`：`IsAvailable`、`RuntimePath`、`Version`、`Diagnostic`；版本未知时为 `null`，不伪造。
  - `IBackendRegistry`：按标识注册/解析；`EngineIdMap` 提供稳定持久化标识（`pi`/`omp`/`codex`/`dsh`），未知标识抛出而不回退。
- `BackendRuntimeProbe.cs`
  - `BackendRuntimePaths`：显式运行时路径（组合根提供，未给出即视为未安装）。
  - `FileBackendRuntimeProbe`：以文件实际存在与否判定可用性，版本由组合根提供，不执行外部进程、不猜测路径。
- `BackendEngines.cs`：`BackendEngineBase` 与四个引擎（Pi/Omp/Codex/Dsh）+ `BackendEngineFactory`（`Create`/`CreateAll`）。
- `BackendRegistry.cs`：线程安全注册表；重复注册抛出，未知标识解析抛出而不回退到默认内核；`CreateDefault` 以探测创建四引擎。

### 2. 方案 B：全局默认 + 会话徽标一键切换

- `BackendPreferenceStore.cs`：全局默认后端（默认 Pi）的原子 JSON 持久化（`backend.json`）；`Changed` 事件、`SetDefault`/`Update`；损坏或未知文件回退到默认并保留原文件供诊断（`LastLoadError`）。
- `SessionBackendBinding.cs`：会话实体携带绑定的后端身份 `Engine` 与持久化标识 `BackendId`；`Timeline` 由绑定自身持有，`Rebind(target, registry)` 先解析目标内核（未注册即拒绝、保持原绑定不变），再重绑执行引擎，已有时间线与消息历史原样保留。
- `src/Fufucode.App/Features/Backend/BackendDescriptor.cs`：视觉元数据 `BackendBadge`（显示名、图标键、专属徽标颜色）；`BackendBadgeCatalog` 约定 Pi=绿色 `#2EA043`、OMP=橙色 `#D97706`、Codex=蓝色 `#2563EB`、DSH=青色 `#0891B2`；`SessionBackendView` 合并绑定与运行时供界面读取。
- `src/Fufucode.App/Features/Backend/SessionBackendSwitcher.cs`：一键切换服务，组合注册表与全局默认存取；`SetDefaultBackend`/`CreateSession`/`Switch`/`Describe`。

### 3. 全 C# 原生 Agent 内核路线契约（Pure C# Agent Engine）

- `PureCSharpAgentContract.cs`：`IPureCSharpAgentEngine`（`RunTurnAsync` / `GetState` / `Reset`）与 `IPureToolDispatcher`（`DispatchAsync`），配套 `PureModelRequest`、`PureAgentMessage`、`PureModelResponse`、`PureToolCall`、`PureToolResult`、`PureAgentTurnResult`、`PureAgentState`。仅定义接口与架构雏形，全程无外部 Node/Python 依赖；具体模型客户端/工具实现后续接入。

## 验证（全部本机实跑）

- 构建：`dotnet build tests/Fufucode.App.Backend.Tests/Fufucode.App.Backend.Tests.csproj -c Debug`
  - 结果：**0 警告、0 错误**（依赖链 Fufucode.Pi / Fufucode.App.Abstractions / Fufucode.Core / Fufucode.App 一并编译通过）。
- 运行：`dotnet run --project tests/Fufucode.App.Backend.Tests/Fufucode.App.Backend.Tests.csproj -c Debug`
  - 结果：**全部通过：100 项断言**，exit 0，无 FAIL。
  - 覆盖：四后端引擎注册与解析（含重复注册/未知标识拒绝）、运行时探测（可用/不可用/未配置）、标识映射往返、全局默认存取与持久化（含损坏回退）、会话后端徽标映射（四色 + 标识一致）、平滑切换（时间线保留、未注册拒绝不改绑定）、纯 C# 契约完整性（接口面 + 文本/工具回合 + 状态重置 + 取消传播）。
- 未执行（按 v3 战略延后）：1480 断言的 App 全量自测。

## 未完成项 / 未验证 / 边界

- **未接线**：`SessionBackendSwitcher`、`BackendRegistry` 等尚未接入 `MainWindow*`/组合根，公共接线归主控 M 统一收编；模块待接线是有效交付。
- **未接入 AppSettings**：全局默认后端以独立 `backend.json` 持久化，未改动共享 `AppSettings.cs`（避免越界修改共享文件）；如需合并到 settings.json，由主控 M 决定。
- **契约雏形**：`IPureCSharpAgentEngine` 仅有接口与测试替身实现，无真实模型客户端/工具实现。
- **运行时探测**：仅以文件存在性判定可用性，未启动真实内核做能力协商；Codex/Dsh 真实协议适配未实现。
- 本记录中的 100 断言只覆盖模块契约与逻辑，不构成整项产品/原生验收。

## 提交与 HEAD

- 代码 + 测试提交：见 `git log`（本任务单一提交）。
- 记录提交：本文件随代码同批提交。
- 收尾时 `git status` 干净、无未跟踪任务文件。
