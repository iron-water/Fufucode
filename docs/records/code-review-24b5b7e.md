# Fufucode 代码合理性与工程性审查报告

- 审查基线：worktree `track-c-b0ae7ded` @ `24b5b7e`（`24b5b7e8e260c62b19b1b5ee46e9326fd4e82789`，分支 `workbuddy/track-c-b0ae7ded`）。注意：`main` 在本基线之后另有 1 个提交（`80ae75d`，轨道 A 的 M3 真实桥接就绪验证），不在本次审查范围内。
- 审查角色：只读审查员。本报告为唯一产出文件，未修改任何源文件。
- 结论摘要：**P0：0 条；P1：4 条；P2：18 条**。未发现正确性层面的事实性错误、数据损坏或凭据泄露路径；4 条 P1 均为"应修"的边界/依赖风险。

---

## 1. 范围与方法

**审查对象**：`src/Fufucode.Pi/`（JSONL 传输、进程宿主、协议 DTO）、`src/Fufucode.Core/`（会话协调、消息重建、持久化与元数据、扩展 UI 路由）、`src/Fufucode.App/`（Avalonia 外壳与时间线，当前为 mock 驱动——M2 集成尚未发生，见 §5）、`extensions/`（fufucode-bridge、demo-extension）、`tests/`（FakePi 与测试质量）、`scripts/`（打包与验证脚本）。

**方法**：全量精读约 6,200 行 C# 与 1,350 行 TS/mjs（代码总量小，逐文件读完）；对照 `docs/AI_DEVELOPMENT_PLAN.md` §6/§7/§8/§11 与 `docs/decisions.md` 冻结契约逐条核对。实际运行取证（本 worktree，未设置任何真实模型环境变量）：

- `dotnet build Fufucode.sln`：成功，0 错误、2 警告（`Program.cs` 重复 using、`SelfTestRunner.cs` 过时 `Bitmap.Save` 重载——与 progress-c.md 记录一致）。
- `dotnet run --project tests/Fufucode.Core.Tests/Fufucode.Core.Tests.csproj`：FakePi 管道全部断言 PASS（M2 Core、M4 持久化、M3 扩展 UI 共 50+ 项）；尾部真实 Pi 冒烟段因本 worktree 无 `runtime/node/node.exe` 抛出 `FileNotFoundException`（环境限制，非代码缺陷，见 §6）。
- `dotnet run --project src/Fufucode.App/Fufucode.App.csproj -- --selftest`：0 项失败（含 500 条长会话 1.00s、无头渲染 22,169 字节位图）。

真实模型与真实 Pi 行为一律以 `docs/verification.md` / `docs/runtime-baseline.md` 的既有记录为准，本次仅核对代码与记录的一致性，未复跑、未设 `FUFUCODE_REAL_MODEL_TEST` / `FUFUCODE_REAL_PERSISTENCE_MODEL_TEST`。

---

## 2. P0（正确性错误、数据或安全风险）

无。核对要点（均为通过）：

- JSONL 拆帧在原始字节流上按 `0x0A` 分帧并容忍前导 `0x0D`（`src/Fufucode.Pi/PiRpcClient.cs:179-182`），多字节 UTF-8 字符天然跨读安全，U+2028/U+2029 不构成边界（FakePi 字节级拆分写入 + `中\u2028文\u2029字` 断言已覆盖）。
- 单写路径（信号量串行 + `Utf8JsonWriter` 结构化写入，`PiRpcClient.cs:20,102-127`）；读泵与分发、UI 分离。
- 有界队列超限显式终止连接（`PiRpcClient.cs:42-45,187,228-229`），符合 decisions.md 的"积压即断连报告"决策；单记录大小上限（`:192`）超限报错不截断。
- `JsonDocument` 生命周期纪律：所有保留的 `JsonElement` 均为 `Clone()`（`PiRpcClient.cs:187`、`PiProtocol.cs:76,83,96,110` 等），无释放后引用。
- 请求超时不自动重发（`PiRpcClient.cs:130-133` 错误文案明示；`ChatSessionCoordinator.cs:186` 注释与 Core.Tests "timed-out side-effect command is not resent" 断言）。
- 会话文件独占：`SessionLease` 用 SHA256 哈希文件名 + `FileShare.None`（`src/Fufucode.Core/AppDataLayout.cs:54-71`），重复打开被拒绝（PersistentTests "session ownership excludes a second writer"）。
- SQL 全部参数化（`DesktopMetadataStore.cs` 全部命令使用 `$` 参数，无字符串拼接）；迁移前先备份并拒绝降级打开（`:45-54`）。
- 凭据：仓库与代码无密钥；测试私有 `models.json` 仅写入环境变量引用 `$FUFUCODE_TEST_SHUSHENG_API_KEY` 而非密钥本体（`PersistentTests.cs:168-184`）；无日志输出 prompt/工具内容/token（当前代码无应用级日志落盘路径）。
- 审批 fail-safe：对话框异常/超时/连接丢失一律按拒绝（`extensions/fufucode-bridge/index.ts:106-119` catch → false；Core 侧超时自动 `Cancel()`，`ChatSessionCoordinator.cs:428-442`）；参数快照在应答前后比对、变更则重问（`index.ts:104,123-126`），符合计划 §8.2"参数被改变后不得套用旧批准"。

---

## 3. P1（应修）

**P1-1 `compaction_end` / `auto_retry_end` 无条件置 `Running`，存在状态机卡死路径**
`src/Fufucode.Core/ChatSessionCoordinator.cs:317-320`：`compaction_end` 与 `auto_retry_end` 直接把 Activity 置回 `Running`，不检查当前状态。若 Pi 在 `agent_settled` 之后发出压缩/重试事件（例如未来的手动压缩入口），Activity 将永久停留在 `Running`：`Immediate` 提交被拒（`:137-138`），外壳只能走 `FollowUp` 排队（排队内容不会执行）或点停止（`StopAsync` 的 abort→`agent_settled` 可恢复）。按 runtime-baseline 与 FakePi `stream` 场景（`agent_end`→retry→`agent_settled`，Core.Tests 已断言 agent_end 非终态），0.87.1 下这些事件均发生在 settled 之前，故属潜在而非现行缺陷。建议：状态转换加守卫（仅在 `Running/Compacting/Retrying` 期间接受），或提供 get_state 对账回退。

**P1-2 prompt 确认超时固定 10s 与扩展命令响应时序冲突**
`ChatSessionCoordinator.cs:158-159`：`prompt` 确认默认 10s；`PiChatSessionAdapter.cs:103-107` 的 `SendAsync` 未传入 `acknowledgementTimeout`，即全链路固定 10s。而 decisions.md（[轨道B] 实测）明确：`/` 命令的 prompt 响应在扩展 handler **完成后**才发——对话框挂起时响应不返回。demo-extension 自身默认对话框超时为 15s（`extensions/demo-extension/index.ts:53`），因此经桌面外壳执行 `/demo.ui-tour` 之类的命令必然在 10s 处报"timed out; execution status is unknown"假错误，而命令实际继续执行并写盘。普通 prompt 不受影响（响应先于 Agent 循环返回）。建议：为 prompt/扩展命令区分确认期限（可配置或放宽），或在文档中明确该边界。**待与实现者确认**预期支持的扩展命令时长上界。

**P1-3 `clear_queue` 响应形状未在真实 Pi 留证，停止链路强依赖该假设**
`ChatSessionCoordinator.cs:470-485`：`ParseClearedQueue` 要求 `data` 内 `steering` 与 `followUp` 两个数组**必须**存在，缺一即抛异常；`StopAsync` 的 catch（`:263-268`）会让整个停止流程失败（诚实——"Never report stopped if the queue may still run"——但用户将无法停止）。`docs/runtime-baseline.md` 能力表与 `docs/verification.md` 均无 `clear_queue`/`abort` 的真实 Pi 验证条目；该形状目前只被 FakePi（自行定义，`tests/Fufucode.FakePi/Program.cs:120-125`）覆盖。**待与实现者确认** 0.87.1 真实 `clear_queue` 响应字段，并在 verification.md 补记。

**P1-4 打包启动器 `PI_CLI` 路径与实际布局不符**
`scripts/packaging/package.ps1:51`：`set "PI_CLI=%~dp0runtime\pi\bundle\cli.js"`，但打包脚本实际把 Pi dist 复制到 `runtime/pi/dist`（`:35-38`），真实路径为 `runtime\pi\dist\bundle\cli.js`（`verify-clean-start.ps1:96,107` 证实）。当前 App 尚未消费该变量（外壳仍是 mock），但这是 M7 集成时的确定性地雷。另外 `Fufucode.ps1` 启动器（`package.ps1:65-71`）完全不设置 Pi 相关环境变量，与 `.cmd` 行为不一致。

---

## 4. P2（建议）

按主题分组，均附证据位置：

**正确性/健壮性**
1. `InputViewModel.SendAsync` 先清空草稿再 await 发送（`src/Fufucode.App/ViewModels/InputViewModel.cs:54-59`）：`SendAsync` 抛出（拒绝/取消竞态/超时）时用户输入已丢失，无处找回。建议失败时回填草稿。
2. 跨库枚举用数值强转映射：`(SessionConnection)(int)state`（`src/Fufucode.Core/PiChatSessionAdapter.cs:256-257`）。当前 Core `ConnectionState/ActivityState`（`ChatSessionCoordinator.cs:7-8`）与 App `SessionConnection/SessionActivity`（`SessionEnums.cs:43-60`）顺序恰好一致，但任何一侧独立重排都会静默错映射。建议改显式 switch 或启动时断言双库序号一致。
3. 两条消息重建路径行为不一致：Core 时间线接受无 `message_start` 的 `message_end`（`MessageTimeline.cs:61-64` else 分支保留该消息），而 `PiChatSessionAdapter.OnProtocol` 直接丢弃（`PiChatSessionAdapter.cs:161-162` `_activeMessageId is null → break`）。按协议 `message_start` 必先到达，现行为可接受，但两处语义应统一并注释。
4. `ReconcileAcceptedPromptAsync` 的 get_state 5s 超时也会把 `Running` 置 `Idle`（`ChatSessionCoordinator.cs:219-224`）：查询失败≠运行结束，属瞬态误标（后续 `agent_start` 会纠正）。建议失败路径不回退 Activity，只记错误。
5. `partialResult` 统一按"最新状态替换"处理（`MessageTimeline.cs:128-131` 注释自认），而计划 §6.6 要求"是否追加由工具契约决定"。对当前覆盖的工具（read 等）替换语义已由 FakePi 与 demo 验证；对未来的流式输出型工具是否正确——**待与实现者确认**。
6. `PersistentSession.OnSnapshot` 在事件泵线程同步写 SQLite（`PersistentSessionManager.cs:150-160`）：状态位翻转时 `SetSessionActive` 为同步磁盘写。与 §6.2"读泵不等磁盘慢操作"的精神有出入；当前每回合仅 2 次翻转，量级可接受，建议后续移到后台队列。
7. `ChatSessionCoordinator._recoverableQueue` 只增不清（`ChatSessionCoordinator.cs:29`），适配器 `_recoveredCount` 单调递增（`PiChatSessionAdapter.cs:119-124`）：长会话多次停止后内存缓慢累积（量级极小）且语义上"已恢复"的文本仍留在快照中。

**工程性/一致性**
8. 测试工程对兄弟产物的隐式依赖：`tests/Fufucode.Pi.Tests/Program.cs:3-5` 与 `tests/Fufucode.Core.Tests/Program.cs:6-8` 硬编码 `tests/Fufucode.FakePi/bin/Debug/net10.0/Fufucode.FakePi.dll` 与绝对 `dotnet.exe` 路径，且无 ProjectReference——干净克隆上单独 `dotnet run` 测试工程会失败，须先全量构建（`verify-m0-m1.ps1:11` 显式先构建 FakePi 说明了这一点）。建议改为运行时探测 + 明确 SKIP 输出。
9. 同上：测试程序末尾的真实 Pi 段在缺 `runtime/node` 时以未捕获 `FileNotFoundException` 崩溃（本次实测）。失败是诚实的（不假造通过），但优雅的"环境缺失→SKIP 并说明"更适合 CI。
10. `scripts/verify-m0-m1.ps1:9` 仍构建 `TestApp/TestApp.csproj`（M0 时代的旧欢迎页），与当前解决方案脱节，应更新或删除。
11. `scripts/bridge-verify/run.mjs:27-29`、`scripts/demo-verify/run.mjs:30-32` 硬编码 `PROJECT_ROOT = "C:\\Projects\\Fufucode"`：脚本从任何其他 worktree 运行时会混用本地扩展与 track-c 工作区的运行时。建议从脚本位置推导根目录或参数化。
12. `package.ps1:35-41` 将 Pi `dist` 与整个 `node_modules` 双份复制进包（`node_modules/@earendil-works/.../dist` 内含同一份 dist），包体积近乎翻倍。
13. `TestApp` 旧工程仍在解决方案中（`Fufucode.sln` 项目 1），是死代码；重命名/清理决策按 AGENTS 归轨道 A，此处仅记录。
14. `src/Fufucode.App/Program.cs:1-2` 重复 `using System;`（CS0105，progress-c.md 已如实记录为已知警告）。
15. `DesktopMetadataStore.ListSessions` N+1 查询（`DesktopMetadataStore.cs:147-157`，每个 id 再开一次连接）；`GetSession` 的 `DateTimeOffset.Parse` 未指定 `InvariantCulture`/`ParseExact("O")`（`:144`）。
16. `extensions/fufucode-bridge/policy.ts:42-50`：配置文件解析失败静默回退默认策略——方向安全（默认 confirm），但用户可能以为自定义策略已生效。建议至少 `ctx.ui.notify` 一条诊断。
17. 计划 §6.5"运行中发送新消息时让用户选择插入指令或排队"未实现：`PiChatSessionAdapter.SendAsync`（`:103-107`）在运行中一律自动 `FollowUp`，`PromptDelivery.Steer` 全库无调用方；UI 无选择入口。属 M2 集成待办，记录在此防遗忘。
18. `StatusBarViewModel._latestUsage` 只在 `MessageFinished` 且携带用量时更新（`StatusBarViewModel.cs:40-41`）：`message_end` 缺 usage 的回合会沿用旧值（显示层面小瑕疵）。

---

## 5. 架构符合性总评

**总体评价：高。分层与计划 §4/§6/§7/§8 的符合度显著好于同规模项目的平均水平。**

逐条核对（§6.1–6.6、§7.1、§8）：

| 计划条款 | 结论 | 证据 |
|---|---|---|
| §6.1 进程启动/退出 | 符合 | 显式可执行路径+参数列表（无 shell 拼接）、`CreateNoWindow`、node 直启 cli.js（`PersistentSessionManager.cs:52-60`）；启动成功以 `get_state` 有效响应为准（`PiRpcClient.cs:81-83`）；正常关闭=关 stdin→有界等待→超时 `Kill(entireProcessTree)`（`:274-288`）；退出/超时后所有等待请求异常收尾（`:265-272`，FakePi "shutdown ends pending request"） |
| §6.2 JSONL 传输 | 符合 | 见 §2；stderr 仅诊断（`PiRpcClient.cs:235-249`，64KB 有界）；请求经结构化序列化写入 |
| §6.3 请求/事件/超时 | 基本符合 | 唯一 ID+命令校验+乱序匹配（`PiRpcClient.cs:97,212-227`）；连接代次 GUID（`:32`，Core 泵过滤代次 `ChatSessionCoordinator.cs:302`）；缺 ID 响应按协议异常处理（`:214-215`，FakePi no_id 用例）；未知事件归 `PiOtherEvent` 不误报（`PiProtocol.cs:58`）。P1-2 是唯一偏差点 |
| §6.4 运行生命周期 | 符合 | 事件先于 prompt 缓冲于通道（泵在 StartAsync 内启动，`ChatSessionCoordinator.cs:112`）；`handled` 不等 settled（`ReconcileAcceptedPromptAsync` + FakePi "handled command does not wait"）；`agent_end` 非终态、`agent_settled` 结合消息/错误判定终态（`ChatSessionCoordinator.cs:321-343`，Core.Tests 两个方向均断言）；`agent_settled` 与 `agent_end` 语义区分明确 |
| §6.5 停止/插入/队列 | 符合（一处待办） | `StopAsync` 严格 clear_queue→保存恢复文本→abort（`ChatSessionCoordinator.cs:245-261`，FakePi 断言命令顺序）；Cancelling 期阻止新提交（`:136`）；恢复文本不覆盖草稿（`InputViewModel.cs:102-125`）。插入/排队的选择入口未做（P2-17） |
| §6.6 消息重建 | 符合 | contentIndex 分块、delta 只追加、`*_end` 换终态块、`message_end` 整体替换（`MessageTimeline.cs:44-105`；App 侧 `MessageRebuilder.cs:160-182` 镜像实现且有自测）；toolcall 未完成 JSON 为 draft 文本（`PiToolCallDraftBlock`）；usage 覆盖式累计（`MessageTimeline.cs:80`，双向测试）；未知用量保持 null（`UsageInfo.IsUnknown`，状态栏显示空） |
| §7.1 四维状态 | 符合 | 连接/活动/待处理 UI 请求集合/最近任务结果分别建模（`SessionSnapshot`，`ChatSessionCoordinator.cs:13-17`），进程故障与模型失败分类恢复入口不同（`SessionFaultKind`/`TaskResult`） |
| §7.2/7.3 数据归属 | 符合 | `LocalApplicationData\Fufucode` 布局（`AppDataLayout.cs`）；`PI_CODING_AGENT_DIR`+`--session-dir` 显式传入（`PersistentSessionManager.cs:52-62`）；SQLite 只存元数据、Pi JSONL 为唯一会话史（类注释与测试双确认）；`get_entries` since 游标=持久化 entry ID（增量验证）；重启先重建再接收新工作（恢复路径全程 `Idle` 门禁，`RestoreHistory:85-86`）；中断不重发（PersistentTests "does not replay the last prompt"） |
| §8 审批/信任 | 符合 | 见 §2 fail-safe 清单；A03 就绪探测命令（`fufucode-bridge.status`，`index.ts:72-80`）；`ApprovalAvailability` 只有 `Unverified/ExtensionError`，不虚报已生效（与 decisions.md 决策一致）；审批期间管道不阻塞（FakePi "progress_while_approval" + bridge-verify a02） |

**层间边界**：Pi 层零聊天语义（`PiRpcClient`/`PiProtocol` 是纯传输+DTO）；Core 不依赖 Avalonia（`Fufucode.App.Abstractions` 独立契约库，纯 BCL）；App 不触碰传输（App 只引用 Abstractions + Mock，尚未引用 Core——M2 集成留待轨道 C，符合 decisions.md 分工）。唯一的跨层耦合点是 P2-2 的枚举数值映射。重复代码少（App 的 `MessageRebuilder` 与 Core 的 `MessageTimeline` 是有意的两级重建，各自服务于事件流/快照模型，语义有差异不算冗余，但见 P2-3）。无明显过度设计；命名一致（Pi 前缀/Core 职责名/Abstractions 冻结面）。

**测试驱动假数据 vs 真实性**：外壳的 mock 性质在代码、UI 标题与进度文档中处处如实标注，未发现假称真实的行为。

---

## 6. 测试质量总评

**总体评价：高，符合计划 §5/§11 的红线，未发现被放宽的断言或复制实现细节的断言。**

- **FakePi 是真实子进程**（真实 stdin/stdout 管道、CRLF、可选字节级逐字节拆分写入，`tests/Fufucode.FakePi/Program.cs:56-68`），不是进程内桩。覆盖：分段（P01/P02）、乱序响应（P03）、stderr 洪水（P04，200KB）、非法 JSON/无 ID/缺字段/命令不匹配/超限（P05）、事件早于响应（P06，"early"）、handled 无运行（P07）、agent_end 后重试（P08）、进程退出与末尾响应竞态（P09，"die"/"respond_exit"）、退出码携带。分段/乱序/退出竞态的模拟真实度达标。
- **断言面向外部行为**：状态经 FakePi `get_state` 回读（`commandOrder`/`promptCount`/`uiReplies`/`uiEffectCount`）、副作用落在 marker 文件、事件流按类型断言——不读实现内部字段。审批 allow/deny/timeout 三态验证了"允许才执行、拒绝与超时无副作用"（marker 文件级，非仅消息级），符合计划 §8.2 的验证要求。
- **诚实性**：真实模型测试全部门控在环境变量之后（`Core.Tests/Program.cs:180,203`），未设变量时明确跳过且打印 `Real model configured` 状态；verification.md 对未验证项（UI 集成、IME 设备、真实桥接工具钩子）如实保留。
- **缺口**：(a) 事件队列积压→`TryWrite` 失败→连接终止这条设计决策（decisions.md 明确记录）没有任何测试直接触发（P2-19）；(b) 测试对兄弟构建产物与绝对路径的依赖（P2-8/9）；(c) bridge-verify/demo-verify 的结果 JSON（`scripts/*/​*-verify-results.json`）已随仓库提交，属可再生的证据快照，可接受。

---

## 7. 未覆盖范围与限制

1. **真实 Pi / 真实模型路径未复跑**：本 worktree 无 `runtime/node`；且按授权约束未设置真实模型环境变量。真实行为结论全部采信 `docs/verification.md`/`docs/runtime-baseline.md` 的既有记录，本次仅核对代码与记录一致性（一致，含 `text_end.content` 字符串形态的修正记录）。
2. **M2 集成未发生**：App 外壳仍是 mock 驱动（`MainViewModel`/`MainWindow` 直接构造 `MockChatSession`）。本次对 App 的审查限于契约符合性、重建器规则与自测质量；真实 Core 接入后的 UI 行为（含 P1-2 的实际表现）未验证。
3. **UI 自动化（§11 第 5 层）**：仅审查了无头 selftest；真实桌面交互（IME 设备、滚动、缩放）未覆盖，progress-c.md 已如实标注。
4. `labs/`（16 个调试脚本）、`reference/`（gitignored）未深入审查；`pi-rpc-smoke-test.mjs` 确认为遗留参考（verification.md 已声明不作为验收证据）。
5. 性能未实测；`MakeSnapshot` 每事件 O(N) 拷贝（`ChatSessionCoordinator.cs:494-497`）在超长会话下的成本留待 M6/M7 基线测量（计划 §12 的节奏）。
6. main 领先本基线 1 个提交（`80ae75d`），其内容不在本报告结论内。

---

*审查方法备注：每条发现附 `file:line` 证据；标注"待与实现者确认"的条目不建议在未确认前直接修改。P1-1/P1-2/P1-3 建议由轨道 A（Core/传输所有者）确认，P1-4 由轨道 F（打包所有者）确认。*
