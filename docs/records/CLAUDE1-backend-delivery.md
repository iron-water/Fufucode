# CLAUDE-1 交付记录：Claude Code 第五外部后端接入

日期：2026-10-16。任务：CLAUDE-1（Claude Code 后端接入）。状态：**module_ready**（模块与定向测试完成；共享组合根接线归主控 M）。

- 工作树：`C:/Projects/Fucode/worktrees/claude1-backend`
- 分支：`agent/claude1-backend`
- 起点：`b394c7d`
- 本记录所在提交：见文末 `commit`（写入后回填于交付卡）

## 1. 实现内容

| 接缝 | 文件 | 变化 |
|---|---|---|
| 后端枚举（统一内核） | `src/Fufucode.Core/Backend/BackendEngineContract.cs` | `EngineId.Claude`；`EngineIdMap.ToStorageId/Parse/TryParse` 增加 `claude` |
| 内核工厂 | `src/Fufucode.Core/Backend/BackendEngines.cs` | `ClaudeBackendEngine`（`ProtocolAdapter`），工厂按枚举创建 |
| 会话后端枚举（持久化） | `src/Fufucode.Pi/BackendProtocol.cs` | `BackendId.Claude`；`BackendCapabilities.For/StorageId/Parse` 增加 `claude`（版本 2.1.296） |
| 桌面启动守卫 | `src/Fufucode.Core/PersistentSessionManager.cs` | Claude 会话显式 `NotSupportedException`，避免落进 OMP 启动分支 |
| 插件目录 | `src/Fufucode.Core/Plugins/PluginMarketplace.cs` | `backend.claude`（`BackendEngine`，默认安装并启用） |
| 徽标/设置项 | `src/Fufucode.App/Features/Backend/BackendDescriptor.cs`、`src/Fufucode.App/ViewModels/SettingsPageViewModel.Features.cs` | Claude 徽标 `#C15F3C` 与设置页第 6 个选项 |
| 新项目 | `src/Fufucode.Claude/` | `ClaudeProtocol`（帧编解码）、`ClaudeStreamClient`（进程 + stdio + 审批回灌）、`ClaudeChatSessionAdapter`（`IChatSession`/`IBackendChatSession`） |
| 测试 | `tests/Fufucode.Claude.Tests/`、`tests/Fufucode.FakeClaude/` | 帧解码/握手/工具往返/session_id 恢复/进程退出；假 CLI 夹具 |

设计要点：

- 只调用本机官方 `claude` CLI，不读、不改、不反编译其源码或二进制；不实现供应商模型协议。
- 不使用 `--dangerously-skip-permissions` 等绕过权限模型的开关；审批走官方 `--permission-prompt-tool stdio`。
- 未知/环境帧（`hook_started`/`hook_response`/`commands_changed`/`thinking_tokens`/`keep_alive`/新字段）宽容跳过，不中断会话；缺少必需字段时不伪造内容。
- 拒绝路径不静默吞掉：`tool_result(is_error)` + `tool_result_meta.permission_decision` 映射为 `ToolCallStatus.Failed` 并保留决策来源。

## 2. 实测取证（本次抓取，非推测）

原始抓取保存在 `docs/records/evidence/CLAUDE1-live-frames.jsonl`（每行含来源文件与是否裁剪）。

| 结论 | 证据 |
|---|---|
| 单轮文本往返：`claude -p --output-format stream-json --verbose` → `system(init)` / `assistant(text)` / `result(success)` | `probe1.ndjson` |
| `init` 帧携带 `session_id` / `model`（可含 `[1m]` 后缀）/ `permissionMode` / `tools` / `capabilities` | `probe1.ndjson` |
| 工具结果回灌为 `user` 帧 `tool_result`，附 `tool_result_meta.permission_decision{decision,source}` | `perm1.ndjson`（accept/config）、`deny1.ndjson`（reject/config） |
| 宿主审批桥：`--permission-prompt-tool stdio` 下 CLI 发 `control_request{subtype:can_use_tool}`；宿主回 `control_response{behavior:allow,updatedInput}` 后 CLI 执行并回灌 `tool_result` | `ptool.ndjson` |
| 拒绝路径：宿主回 `{behavior:deny,message}` → CLI 回灌 `is_error:true` 的 `tool_result`，模型可见理由 | `deny1.ndjson` |
| `interrupt`：`control_request{subtype:interrupt}` → `control_response{still_queued:[]}`，回合以 `result` 收束 | `deny1.ndjson` |
| 多轮：同一进程连续 `user` 帧保持上下文，每条用户消息各产生一次 `init`+`assistant`+`result` | `mt-2.ndjson`（`C:\tmp\clprobe`） |
| `--resume <session_id>`：新进程 init 返回同一 `session_id` 并恢复上下文（模型答出上一轮记忆值 `ZEBRA42`）；非法 id 返回 `result{is_error:true,subtype:error_during_execution}` 且无 init | `resume-1/2/3.ndjson` |
| `--include-partial-messages`：`stream_event{content_block_delta,text_delta}` 提供打字机增量 | `partial1.ndjson` |
| 环境事实：本机 `ANTHROPIC_BASE_URL` 经 magpie，`model` 形如 `group/auto-deepseek-v4-1-flash[1m]`；已装 SessionStart 钩子，启动产生 `hook_started/hook_response` | `probe1.ndjson`、`C:\Users\mio\.claude\settings.json` |

## 3. 实际验证（本轮真实执行）

| 验证 | 命令 | 结果 |
|---|---|---|
| 全量构建 | `dotnet build Fufucode.sln` | **0 警告 0 错误**（含新增 3 个项目） |
| Claude 定向测试 | `dotnet run --project tests/Fufucode.Claude.Tests` | **115 项断言全过**（帧解码 + 假 CLI 进程往返） |
| 既有后端回归 | `dotnet run --project tests/Fufucode.App.Backend.Tests` | **491 项断言全过**（含更新后的六后端枚举/徽标/插件计数） |
| Pi 会话回归 | `dotnet run --project tests/Fufucode.Core.Tests -- --d1-only` | Pi 段 **13 项全过**（Claude 守卫未影响 Pi 路径） |
| 真实 CLI 单轮（最便宜模型） | `dotnet run --project tests/Fufucode.Claude.Tests -- --live <claude.exe> devin/swe-2 "Reply with exactly: PONG"` | 握手 `Connected`；`sessionId=79b73f5e-…`；`outcome=Completed`；`text=PONG`；进程退出被观测 |
| 真实 CLI 工具审批往返 | `... --live-tool <claude.exe> devin/swe-2` | CLI 发 `can_use_tool(Write)` → 宿主 allow → `tool_result` 回灌 → 工具块 `Completed`、`permission=accept (user_temporary)`、目标文件真实创建、`outcome=Completed` |

## 4. 未执行 / 未验证

- **OMP 段 D1 回归未运行**：本工作树与主仓均缺 `runtime/omp/omp.exe`（`runtime/` 被 gitignore），属既有环境差异，与本次改动无关。Pi 段已通过。
- **`--include-partial-messages` 未在产品路径启用**：适配器已实现 `stream_event` 增量映射并有单元覆盖，但默认启动参数未开启该开关（打字机依赖完整 `assistant` 帧逐块落地）。是否启用由主控决定。
- **中断（Stop）未做真实 CLI live 验证**：`interrupt` 控制帧形状已实测（见上表），适配器实现已就位；未在真实 CLI 上端到端跑 `StopAsync`。
- **项目分叉（ProjectFork）未验证**，故 `BackendCapabilities.For(Claude).ProjectFork=false`。
- **产品级验收未做**：桌面共享组合根尚未接线，Claude 会话无法从设置页/主界面真实创建；本交付为模块就绪。

## 5. 公共接线需求（交主控 M）

1. `PersistentSessionManager` 增加 Claude 启动分支：可执行文件路径（默认解析 `claude.exe`）、`ClaudeProtocol.BuildArguments(model, resumeSessionId)`、会话目录与 `--resume <session_id>` 持久化（`SessionRecord.BackendSessionId`）。
2. `AppOptions`/组合根注入 Claude 运行时路径；`BackendRuntimePaths.Set(EngineId.Claude, path)` 后由 `BackendRegistry.CreateDefault` 暴露可用性。
3. 桌面审批 UI 订阅 `ClaudeChatSessionAdapter.PermissionRequested` 并调用 `RespondToPermissionAsync`（当前 `IExtensionUiSession` 为 Pi 专用，未强行复用以免破坏既有契约）。
4. 如需打字机增量，启动参数追加 `--include-partial-messages`（`ClaudeProtocol.BuildArguments(..., includePartialMessages: true)`）。

## 6. 恢复与临时状态

- 为跑 `Fufucode.Core.Tests --d1-only` 的 Pi 段，临时复制了 `runtime/node/node.exe` 并给 `runtime/pi/node_modules` 建了目录联接（junction）；两者均已被 `.gitignore` 忽略，**验证后已删除/解除**，工作树恢复原状。
- 协议探针产物位于工作树外的 `C:\tmp\clprobe\`（`.ndjson` 原始抓取），未纳入仓库；关键帧已裁剪副本存入 `docs/records/evidence/CLAUDE1-live-frames.jsonl`。
- 未修改 `reference/`，未推送，未改写历史，未创建/删除工作树。
