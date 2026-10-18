# P1-BACKEND 任务书：多核心后端矩阵与一键切换 (Pi/OMP/Codex/DSH + 全C#内核路线)

难度：L3 ｜ 推荐模型：`group/auto-deepseek-v4-1-flash`（显式 max 思考强度）或 `group/auto-hy4-preview-f`
任务类型：受管特种兵独立功能包 ｜ 起点 HEAD：`3c03b44b0f05395b1ebfc506e802f862f5d51ecb`
工作树绝对路径：`C:/Projects/Fucode/worktrees/p1-backend`
分支：`agent/p1-backend`

## 1. 允许修改路径
- `src/Fufucode.Core/Backend/**`（新建或扩展核心接口与契约）
- `src/Fufucode.App/Features/Backend/**`（新建或扩展前端视图模型与服务）
- `tests/Fufucode.App.Backend.Tests/**`（新建独立测试工程）
- `docs/records/P1-backend-matrix.md`（本任务记录文档）

## 2. 严禁触碰路径
- 严禁修改 `MainWindow.Workspace.cs`、`MainWindow.axaml` 等共享外壳文件；外壳公共接线由主控 M 统一收编。

## 3. 功能目标与验收标准
1. **多核心后端引擎注册与统一契约 (IBackendEngine / IBackendRegistry)**：
   - 统一抽象四种执行内核：`Pi`（默认 Pi RPC）、`Omp`（Oh My Pi 独立引擎/CLI 进程契约）、`Codex`（Codex 协议适配）、`Dsh`（DeepSeek Harness 协议适配）。
   - 提供标准运行时探测与可用性状态：`IsAvailable`、`RuntimePath`、`Version`、`EngineId`。
2. **方案 B：全局默认 + 会话徽标一键切换**：
   - 设置中支持指定“全局默认后端”（默认为 Pi）；
   - 每个会话实体（Session）携带其绑定的后端身份 `BackendId`，并提供视觉元数据（显示名称、图标、专属徽标颜色：Pi=绿色、OMP=橙色、Codex=蓝色、DSH=青色）；
   - 会话切换后端时保持已有时间线与消息历史，重新绑定执行引擎。
3. **全 C# 原生 Agent 内核路线契约 (Pure C# Agent Engine)**：
   - 建立 `IPureCSharpAgentEngine` 接口与自包含架构雏形，定义无外部 Node/Python 依赖的纯 C# 循环接口（模型请求、工具调度、状态管理）。
4. **独立单元测试**：
   - 编写 `tests/Fufucode.App.Backend.Tests` 验证：
     1. 四后端引擎成功注册与解析；
     2. 全局默认后端存取与回退规则；
     3. 会话后端徽标与元数据映射；
     4. 会话后端平滑切换不丢消息历史；
     5. 纯 C# 内核契约完整。
   - 构建 `dotnet build tests/Fufucode.App.Backend.Tests` 达到 0 警告、0 错误。
   - 依 v3 战略延后全量 1480 断言与原生挂机测试。

## 4. 人类授权与交付回传协议
- 本任务获用户直接授权：
  > 授权来源：主agent (提问) 01a10ae0-9bb9-78f3-b2b3-fd3ceeb34d07
  > 用户原文：“我明确授权：你向本项目受管执行会话派单、返工和协调；这些执行会话向你所在的主控线程主动回传交付、阻塞和协调消息。派单时附本授权原文和真实主控threadId。”
  > 主控线程 ID：`01a10ae0-9bb9-78f3-b2b3-fd3ceeb34d07`
- 任务收尾时必须在 `docs/records/P1-backend-matrix.md` 中完整记录：状态、真实 HEAD、改动、测试结果、未完成项、接线需求；
- 提交文档并确认 `git status` 干净后停写（`writerStopped: true`）；
- 使用 `send_message_to_thread` 向主控线程发送唯一最终交接卡（严格单个 JSON，禁止任何前置或后置闲聊）。
