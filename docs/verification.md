# 验证记录

> **状态：HISTORICAL LOG（2026-10-08 起）**
> 本页是历史验证记录，不代表当前源码。最新设置/接线验证见 [records/frontend-wiring-normalization-2026-10-20.md](records/frontend-wiring-normalization-2026-10-20.md) 和 [records/INDEX.md](records/INDEX.md)。

## 2026-10-08：Pi 1.1.0

- 实际落锁 Pi 1.1.0 探针：11 PASS、0 FAIL/SKIP；自测 14 PASS。含双向 JSONL 副本读写、真实审批桥，以及显式内置 codemode/MCP 的 QuickJS/stdio/内层审批/store 行为。
- solution 构建 0 错误/警告；Pi tests 21 PASS；升级后 Core tests 333 PASS、退出 0，Atria 真实模型验收明确 SKIP。App 无头自测退出 0、0 项失败。
- 合成 SSE 不是云模型验收。远程 MCP/OAuth、Durable、子代理、生产会话升级/回退演练未运行。完整产物与包内验证见 `docs/records/pi-upgrade-2026-10-08.md`。
- Pi 1.1.0 新包的精简 PATH/私有 cwd App 全量 selftest、两次 get_state、正常退出/无残留均 PASS；归档及自测后完整性检查 15,542 文件通过。1.4.1 安装器编译退出 0，未实际安装。

## 2026-10-06：R05 正式工作台

- App 构建 0 错误/警告，36 项 R05 定向检查、17 项设置目的地检查、Core 套件退出 0。
- 真实已安装 Pi + 本地模型验证八种权限/工具组合、图片协议载荷、分叉身份/继续发送/子会话重开；工作台检查 0 失败。
- 检查 1440×900、800×640 实际控件渲染截图。系统文件选择器、物理拖放、音频实际播放、外部编辑器、在线发布查询未逐项人工验收。
- 完整对照与证据路径：`docs/records/r05-workbench-2026-10-06.md`。

2026-09-28：

- `dotnet build TestApp/TestApp.csproj --no-restore`：成功，0 警告、0 错误。
- `runtime/node/node.exe .../cli.js --version`：`0.87.1`。
- `runtime/node/node.exe scripts/pi-baseline.mjs`：真实 Pi 的 `get_state`、`get_available_models` 成功，stdin 关闭后 exit code 0；没有发送 prompt。
- `dotnet run --project tests/Fufucode.Pi.Tests/Fufucode.Pi.Tests.csproj`：FakePi 的乱序 ID、拆分 UTF-8 与 Unicode 分隔符、多记录写入、事件代次、stderr 压力、超时与取消、进程退出及末尾响应、非法 JSON、无 ID/缺字段、命令不匹配、记录超限均通过；真实 Pi 两项查询通过。
- `pwsh -NoProfile -File scripts/setup-runtime.ps1`：从锁文件重装 Pi 依赖成功；随后再次运行无模型 RPC 查询成功。
- `pwsh -NoProfile -File scripts/verify-m0-m1.ps1`：从新的 PowerShell 进程完成 Avalonia 构建、FakePi 测试、真实 Pi 查询和正常退出。具体断言以该脚本输出为准。

M0/M1 阶段尚未验证桌面 UI、真实模型、`agent_settled` 实际运行或打包后的独立启动；真实模型与 `agent_settled` 的后续 M2 Core 结果见下节。现有 `pi-rpc-smoke-test.mjs` 会发送真实 prompt，且使用用户全局包路径，因此只保留作后续参考，不作为 M0/M1 验收证据。

## M2 Core（2026-09-28）

- `dotnet build Fufucode.sln`：Core、Pi、FakePi、测试项目、独立契约类库、App 外壳及原 TestApp 均成功构建，0 警告、0 错误。
- `dotnet run --project tests/Fufucode.Core.Tests/Fufucode.Core.Tests.csproj`：FakePi 真实管道下两种响应/事件先后顺序、文本/思考/工具块、最终消息替换、累计 usage、工具结果替换、`agent_end` 非终态、清队列再停止、prompt 超时不重发、handled 无运行、模型失败及进程故障区分均通过；真实 Pi 无模型查询通过。
- `FUFUCODE_REAL_MODEL_TEST=1` 重跑 Core 测试：所选真实模型在同一 Pi 进程中两轮对话均收到非空 assistant 文本并以 `agent_settled` 完成。初次执行暴露 `text_end.content` 为字符串的协议差异，已依据安装包类型定义修正并复测通过。
- C 冻结接口适配测试：FakePi 管道下 `IChatSession` 的连接/活动、分块事件、最终消息与累计用量、工具状态、排队与清队列恢复、模型失败、协议错误和进程退出分类均通过。
- UniGetUI 设计参考：采纳已知 prompt 命令 DTO 的 source-generated `JsonSerializerContext`；继续使用按 LF 分帧的字节读取和明确的进程宿主，不采用其面向 CLI 进度行的 CR 分帧、静态队列或泛化 `_UnSafe` 层。

尚未验证：App 外壳接入真实 Core 后的桌面交互；这属于后续 M2 集成，不计入 A 轨道 Core 完成状态。

## M4 Core（2026-09-28）

- `dotnet build Fufucode.sln --no-restore`：成功，0 警告、0 错误。
- `dotnet run --project tests/Fufucode.Core.Tests/Fufucode.Core.Tests.csproj --no-restore`：FakePi 真实管道覆盖专属启动参数、完整与 `since` 增量历史、压缩边界、遗弃分支、关闭重开后不重复、会话名/草稿/偏好、同会话独占、中断不重发；还验证了 assistant 已产生工具调用但进程在 `agent_settled` 前退出时，重开仍显示 Interrupted 且不重发。SQLite v1→v2 迁移保留数据且生成可打开的修改前备份。原 M2 Core 测试继续通过。
- 同一测试内的真实 Pi 无模型冒烟：`--session-dir` 指向应用专属目录，`get_entries` 与会话命名成功，同 ID 重开成功。首次启动时 `sessionFile` 是未来路径，JSONL 尚未创建；Core 只在文件实际存在后记录可恢复路径。未声称此冒烟验证了对话写盘。
- 首次设置 `FUFUCODE_REAL_PERSISTENCE_MODEL_TEST=1` 发起真实模型请求时，私有配置未提供 API key，Pi 返回 `No API key found for the selected model`；该次没有验证对话写盘。用户随后提供 shusheng 配置，补验结果见下节。

## M3 Core 桌面侧及 M4 模型补验（2026-09-28）

- `dotnet build Fufucode.sln --no-restore`：构建成功。App 中 2 条现有编译警告，A 轨道代码 0 错误。
- `dotnet run --project tests/Fufucode.Core.Tests/Fufucode.Core.Tests.csproj --no-restore`：FakePi 审批允许/拒绝/超时、严格一次应答、选择项约束、通知不入待请求集合、`extension_error` 诊断、审批期间命令与事件继续运行、`agent_settled` 并存、关闭与旧连接代次隔离通过。真实 Pi 加载 `tests/fixtures/approval-ui-fixture.ts` 后，三条路径均完成；只有允许写入临时文件，拒绝和超时保持原内容。无需模型调用。
- 用户指定的 CC Switch shusheng 配置用于 M4 真实模型补验。测试从 CC Switch 本地配置取得密钥，经临时环境变量注入 Pi；临时 `models.json` 只保留变量引用，未向仓库写入密钥。`FUFUCODE_REAL_PERSISTENCE_MODEL_TEST=1` 下，真实模型回合以 `agent_settled` 完成、会话 JSONL 写盘、关闭重开后恢复及继续同一会话、续聊写回原文件，均通过。
- 尚未验证：B 轨道桥接扩展实际工具调用钩子、App 审批弹窗交互与真实工具拒绝后的无副作用；这些不属于本次桌面侧临时扩展的通过结论。桥接扩展无就绪握手前，Core 的审批状态保持 `Unverified`。

## 桥接集成收口（2026-09-29）

- `git merge main`（A 工作树已在 main）：输出 `Already up to date.`。工作树原有未提交 `.gitignore` 修改与本次 A 轨道改动无关，未触碰。
- `dotnet build Fufucode.sln --no-restore`：构建通过；App 轨道有 2 条既有警告，A 轨道代码无错误。
- `dotnet run --project tests/Fufucode.Core.Tests/Fufucode.Core.Tests.csproj --no-restore`：FakePi 验证三态探针、扩展缺失/启动失败、未知 method 忽略、无 Pi timeout 的 editor 本地取消、连接丢失清理待审批；原 M2/M4/M3 Core 断言继续通过。
- 同一 Core 测试启动真实 Pi 0.87.1 与 `extensions/fufucode-bridge/`，用 B 轨道的本地 stub 模型生成真实 `write` 工具调用：允许后中文文件实际生成，拒绝及超时后文件均不存在。三个场景均检查原审批请求中的目标路径；审批等待时 `get_commands` 继续应答。无桥接扩展时探针返回 `Unavailable` 且产品模式拒绝提交 prompt；坏扩展令 Pi 退出时 Core 也返回 `Unavailable` 并保留启动诊断。`PersistentSessionManager` 的真实 Pi 冒烟检查标准运行时布局自动加载桥接并返回 `Verified`。测试临时证据目录位于 `tmp/bridge-core/`，文件系统断言见 `tests/Fufucode.Core.Tests/BridgeIntegrationTests.cs`。
- 尚未验证：C 轨道 App 弹窗到真实桥接的 UI 交互；F 轨道发布包中桥接扩展的复制及包内探针；真实云模型对工具调用的非确定性行为。本次工具审批验收使用真实 Pi/真实扩展与本地 stub 模型，shusheng 的真实模型持久化结果仍见上一节。
- 最终复跑 `dotnet build Fufucode.sln --no-restore`：0 警告、0 错误；Pi.Tests 与 Core.Tests 均通过。Core.Tests 再次从用户指定的 CC Switch shusheng 配置注入临时进程环境变量，在桥接扩展自动加载后，真实模型对话写盘、关闭重开及续聊均通过；这验证了桥接加载与既有聊天/持久化路径兼容，不代表云模型自动触发了工具审批。
- 修正扩展命令对话框挂起时的停止/关闭顺序后，重跑 Core.Tests：真实 Pi 临时扩展中，Stop 和 Close 均在 10 秒上限内结束，测试标记文件仍保持原内容，待处理请求清空。上述构建与 Pi.Tests 结果来自该修正前；修正后的 Core.Tests 已通过。

## 审查 P1-1/2/3 验证（2026-09-29）

- A 工作树位于 `main`，执行 `git merge main` 得到 `Already up to date.`；原有未提交 `.gitignore` 修改未触碰。
- `dotnet build Fufucode.sln`：成功，0 错误、1 条 App 既有 `Bitmap.Save` 过时警告。合并后首次 `--no-restore` 构建使用旧 App 资产文件而缺 Pi 引用；正常恢复构建成功。
- `dotnet run --project tests/Fufucode.Core.Tests/Fufucode.Core.Tests.csproj --no-restore`：退出码 0。FakePi 验证晚到 `compaction_start/end`、`auto_retry_start/end` 不会将 settled 后的 Idle 重新置为 Running，且下一条 Immediate prompt 可发送；可配置的普通短确认期限与扩展命令较长期限分别生效。
- 同一测试的真实 Pi 0.87.1 临时扩展：斜杠命令审批保持挂起 11 秒后才应答，prompt 成功确认，批准后标记文件写入；这直接覆盖 P1-2 原 10 秒假超时边界。真实 Pi 空会话 `StopAsync` 成功：Core 的 `ParseClearedQueue` 在 `abort` 前严格要求 `clear_queue.data.steering` 和 `followUp` 均为数组，因此该通过验证了 P1-3 的响应形状。D 轨道的 `src/Fufucode.App` `--selftest --realpi` 也记录了相同停止链路通过（见 `docs/archive/progress/progress-d.md`）；两次均未覆盖非空队列条目的实际内容。
- P1-1 是晚到事件的防护性修复；真实 Pi 0.87.1 常规流中未观察到 settled 后的压缩/重试事件。P1-2 超过可配置命令期限时依旧报告执行状态未知，不自动重发。

## F01 项目信任 Core 路径（2026-09-29）

- `git merge main`：A 工作树在 `main`，结果 `Already up to date.`；原有 `.gitignore` 未提交修改未触碰。
- `runtime/node/node.exe scripts/verify-project-trust.mjs`：Pi 0.87.1 RPC 四组隔离启动全部通过。默认未决与 `--no-approve` 跳过项目扩展和技能；`--approve` 加载二者；`--approve --no-extensions` 仅加载技能。项目与 Pi 配置均在忽略的 `tmp/project-trust-probe/`，无模型调用。一次初跑因脚本把 `get_commands` 技能名称误写为不带 `skill:` 前缀而失败；按真实响应修正断言后复跑通过，未改 Pi 或 Core 行为。
- `dotnet build Fufucode.sln --no-restore`：成功，0 错误，App 两条既有警告（CS0618、AVLN3001）。`dotnet run --project tests/Fufucode.Core.Tests/Fufucode.Core.Tests.csproj --no-restore`：退出码 0。FakePi 验证受保护资源触发决定、未决时不创建会话也不启动、拒绝/信任分别传 `--no-approve`/`--approve`、决定跨 manager 重建保留、祖先 `.agents/skills` 也触发提示；原 Core 真实 Pi 与桥接验证继续通过。
- D 轨道 UI 尚未接入 `ProjectTrustDecisionRequiredException` 的选择弹窗；用户在有受保护资源的未决项目首次启动时，当前 App 会显示启动失败诊断。UI 接线约定见 `docs/decisions.md`。本次未验证桌面弹窗的交互体验。

## M6 Core 基础与 P2-4/6/7/15（2026-09-29）

- `dotnet build Fufucode.sln --no-restore`：最终增量构建成功，0 警告、0 错误；本次较早的完整构建显示 App 既有 CS0618 与 AVLN3001 两条警告。`dotnet run --project tests/Fufucode.Pi.Tests/Fufucode.Pi.Tests.csproj --no-restore`：退出码 0，真实 Pi 查询继续通过。
- `dotnet run --project tests/Fufucode.Core.Tests/Fufucode.Core.Tests.csproj --no-restore`：最终退出码 0。P2-4：FakePi 让已接受 prompt 后的 `get_state` 返回失败，状态仍 Running/Unknown；P2-6：SQLite 独占锁阻塞元数据写时，RPC 流仍在 2 秒内收敛；P2-7：连续两次停止分别只向 UI 报告当次队列文本；P2-15：多会话列表在 `fr-FR` 文化下完整读取并保持时间戳。设置测试覆盖读写、通知、含凭据 URL 拒绝、旧格式迁移、损坏文件回退和修复前备份、较新版本防覆盖；兼容端点导出只含环境变量引用。日志测试用敏感标记作 prompt 及任意标签，落盘文件含请求元数据且无标记。
- `dotnet run --project src/Fufucode.App/Fufucode.App.csproj --no-build -- --selftest --realpi`：最终退出码 0，0 项失败；真实 Pi 连接、会话切换/恢复、扩展命令与工具审批允许/拒绝、两轮 Atria 模型对话均通过。首次并发会话运行时暴露两个日志写入器争用同一日文件；改为每会话独立日志文件后复测通过。一次 Core 桥接超时断言与该自测并发时失败，单独顺序复跑通过，未修改审批逻辑。
- 未验证：Windows PasswordVault 凭据实现、设置 UI 到 Pi 配置的应用、用户提供的真实代理/自定义端点，以及 `HistoryMapper` 去重候选。这些均不计作 M6 完成。应用级诊断只记录固定元数据，不包含完整协议录制。

## A 轨道 2026-09-29 待外部验证增量

- 源码已新增 Windows Credential Manager 凭据解析、默认接线、环境变量回退，以及 `DiagnosticExport.WriteJsonLinesAsync` 和对应 Core 用例。此次没有执行任何构建或测试，故这些均为未验证实现。
- 外部验证顺序：构建解决方案；运行 Core 测试；在 Windows 以临时目标名执行凭据保存、读取、删除及环境变量回退；运行真实 Pi 的新会话模型/思考等级、代理和兼容端点路径；核对诊断导出仅包含固定元数据。
- 当前凭据实现采用 Win32 Credential Manager API；严格的 WinRT `PasswordVault` API 兼容性尚未验证。F 轨诊断包可调用 Core 导出流，包的其余文件及完整性仍由 F 轨负责。

## M5 用户扩展加载接口与 F05（2026-09-29）

- A 工作树 `main` 上先执行 `git merge main`，返回 `Already up to date.`；D 的交接依据为 `95886cf` 的 `docs/archive/progress/progress-d.md` 新增章节。本地 Pi 0.87.1 CLI 文档明确 `--extension` 可重复、`--no-extensions` 仍允许显式路径。
- `dotnet build Fufucode.sln --no-restore`：成功，0 错误；本次完整构建显示 App 既有 CS0618、AVLN3001 两条警告。
- `dotnet run --project tests/Fufucode.Core.Tests/Fufucode.Core.Tests.csproj --no-restore`：退出码 0。FakePi 验证桥接先于两个用户扩展、`--no-extensions` 保留、选择列表改变不影响既有进程、新进程按新列表加载；缺失路径在创建会话前抛带路径类型化异常；项目内扩展在未决/拒绝/信任三种状态分别阻断、阻断、加载。真实 Pi 0.87.1 的 `get_commands` 证明两个显式用户扩展与桥接同时注册，关闭后新进程不再加载被禁用扩展；主动抛错的用户扩展被识别为 `PiStartupFailure` 并关联原路径。F05 FakePi 注入 `message_end.stopReason=aborted` 与 `errorMessage=request aborted`，结果为 Interrupted 且无 `SessionError`。
- `dotnet run --project src/Fufucode.App/Fufucode.App.csproj --no-build -- --selftest --realpi`：退出码 0，0 项失败；默认空用户扩展列表下真实 Pi 启动、会话切换与恢复、两轮 Atria 对话、工具审批允许/拒绝均保持通过。
- 未验证：D 管理页对 `UserExtensionPaths` 的赋值、启停持久化和真实 UI 异常展示；这属于 D 独占路径，A 未改动。用户扩展是同权限代码，显式选择及项目信任不会形成沙箱。

## Track A endpoint removal (2026-09-29)
- Added a Core test for removing an owned compatible provider after the endpoint setting is cleared. The test has not been run in this sandbox. External driver should run the Core suite, then verify the real Pi endpoint path.

## Track A model state handoff (2026-09-29)
- Added a Core assertion that a newly created session's chat adapter reports the model selected by `set_model` and confirmed by `get_state`. Not run in this sandbox; external driver should run the Core suite and real Pi M6 acceptance.

## Track A2 authored acceptance (2026-09-30, not executed)

- `dotnet build tests/Fufucode.FakePi/Fufucode.FakePi.csproj`
- `dotnet run --project src/Fufucode.App/Fufucode.App.csproj -- --selftest`
- `dotnet run --project tests/Fufucode.Core.Tests/Fufucode.Core.Tests.csproj`
- `dotnet run --project src/Fufucode.App/Fufucode.App.csproj -- --selftest --realpi`
- Optional Atria Core acceptance: set process environment variables `FUFUCODE_ATRIA_REAL_MODEL_TEST=1` and `FUFUCODE_ATRIA_API_KEY`, then rerun Core tests. The acceptance uses a temporary Pi agent directory, child-process key injection, explicit `set_model` plus `get_state` confirmation, two context-dependent real model turns, and `StopAsync` after each turn.

The source added by this dispatch has not been built or executed. Do not treat these commands as passing evidence until an external driver runs them. App's Atria selftest was not edited because `src/Fufucode.App/` belongs to track D.

## R05 full-scope verification (2026-10-07)

- Final App build to `tmp/r05-full-build`: 0 errors and 0 warnings.
- `--selftest --r05-only`: 61 checks passed, 0 failed.
- `--selftest --settings-destination-only`: 17 checks passed, 0 failed.
- `node scripts/workbench-demo-verify.mjs tmp/r05-full-build/Fufucode.App.dll`: exit 0, Workbench failures 0, real installed Pi with a local scripted model, 3 HTTP requests. Includes native fork-dialog title-bar cancellation and runtime message identities.
- Evidence and unverified native hardware/material boundaries: `docs/records/r05-full-scope-2026-10-07.md`.

## R05 sidebar tabs (2026-10-07)

- `--tabs-only`: 53 checks, 0 failures; all nine page lifecycles and native file/diff/quote/outline paths.
- Real WebView2: 11 checks, 0 failures; back, forward, reload and lifecycle.
- Real ConPTY smoke and native terminal Input/Run output passed.
- Workbench driver: exit 0; real Pi overview new/restore, isolated side chat and rename, 4 HTTP requests.
- Core `--a13-only`: exit 0, including staged/unstaged, new and binary file diffs.
- R05 61 and settings 17 checks still passed. No all-repository-green claim; early legacy suite failures and physical UI boundaries are recorded in `docs/records/r05-tabs-2026-10-07.md`.
