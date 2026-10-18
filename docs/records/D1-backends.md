# D1 双后端最小闭环

日期：2026-10-10。状态：提交待验收；G1 由 M 收编后复核，本记录不解锁下游。

## 开工身份与边界

- 软件：Codex desktop，本地 PowerShell 执行会话；D1，L4（协议、身份、审批、恢复跨模块）。建议 S 档负责关键实现，用户决定会话模型；验证优先免费模型，不自动换模型。
- 独立工作树：`C:\Users\mio\.codex\worktrees\f80f\Fufucode`，detached HEAD；起点 `83045c92eaeff44b2a69ca2d88b4b541cc68f4e0`。开工工作树干净，无继承用户改动。
- 开工 `git worktree list`：主区 `C:/Projects/Fucode/Fufucode` main@83045c9；OMP 工作树 codex/omp-work@af9e1f6；本工作树 detached@83045c9；历史 herdr 工作树 brave-river-ba40@b8dc6c9。仅本工作树写入。
- 已读 AGENTS、AI_DEVELOPMENT_PLAN、execution-contract、session-handoff-protocol、launch-prompts 的 D1 完整段落、next-stage-roadmap，以及 management-surfaces、complete-feature-modules、new-product-capabilities、runtime-baseline。
- 范围：Pi/Core 会话与持久化、App Integration/必要入口、D1 工装及记录。未修改第三方 reference、其他任务实现、权威 progress/decisions。未整合 main、未 push、未清理工作树。

## 已交付接口与实际协议

| 项目 | Pi | OMP |
| --- | --- | --- |
| 本轮实测版本 | 1.1.0，Node 24.15.0 | @oh-my-pi/pi-coding-agent 18.8.5 |
| 启动 | Node + Pi CLI，`--mode rpc` | `omp.exe --mode rpc` |
| 身份/恢复 | 桌面 ID 对应 `--session-id`；`--session file` | 桌面 ID 与引擎 UUID 分开存储；`--resume file` |
| 命令发现 | `get_commands` | `get_available_commands` |
| 停止 | `clear_queue` 撤回文本队列，再 `abort` | `abort_and_restore_queue` 原子撤回并停止；队列项读取 text，截断明确报错 |
| 完成 | `agent_settled` + assistant 结果 | `prompt_result` 的 status/sessionSettled、`session_settled`；不以 agent_end 或 prompt ACK 结束 |
| 审批 | 已有 fufucode-bridge 探针与匹配 ID 回复 | 同一桥接经真实引擎验证；不宣称一般 Pi 扩展兼容 |
| 项目分支/插件 | 保持既有 Pi 路径 | ProjectFork=false，分支调用拒绝；既有 Pi 扩展管理入口拒绝，O5 后续处理 |

- `Fufucode.Pi.BackendId`、`BackendCapabilities.For` 提供身份和已验证能力；VerifiedVersion 是本轮基线，**不是动态版本协商或对任意版本的支持承诺**。
- `DesktopMetadataStore.SessionRecord` 新增 Backend、BackendSessionId；schema 4，迁移前备份，旧记录默认 pi。BindBackendSession 只允许首次绑定或同 ID 重绑；未知 backend 不替换引擎。
- `PersistentSessionManager.CreateAsync(..., backend: BackendId.Pi/Omp)` 创建，ResumeAsync 按记录恢复；会话文件和引擎身份必须匹配。OMP 历史放项目目录下 omp 子目录，配置放数据根 omp-agent；旧 Pi 目录不搬迁。
- `ChatSessionCoordinator.Backend/Capabilities` 与版本专属命令适配保持既有 RPC 进程边界，不重写 Agent 循环。`PiChatSessionAdapter` 保留名称以兼容调用方，但可承载两种后端，映射同一 IChatSession 事件并对 OMP 终止事件去重。
- App Abstractions 新增 `IBackendChatSession`（BackendId/BackendName 字符串）；ViewModel 仅消费身份。`RealSessionHost.NewSessionAsync(..., backend: ...)`、原有发送/停止/审批接口已接线。
- 桌面“新聊天/新建会话”选择 Pi/OMP，默认 Pi；创建后固定，恢复不转换后端。CLI `--backend pi|omp` 选择启动/创建偏好，`--session` 仍服从所存身份；`--omp-exe <absolute-path>` 为显式开发运行时覆盖。
- 默认 OMP 落点 `runtime/omp/omp.exe`；**本工作树未随包分发 OMP**。缺少时明确报错，不静默使用 PATH 或替换 Pi；打包安装属于 D0/R/G3。
- 私有 OMP models.yml 只写桌面配置的兼容端点；apiKey 保存环境变量名，不写凭据值，不导入/修改用户全局 OMP 配置。拒绝覆盖无桌面所有权标记的已有 models.yml。OMP 禁用自动扩展/skills/rules/LSP；内建审批设 yolo 以避免与已验证桌面工具桥双重拦截，产品默认桥接未验证时禁止提交 prompt。此机制不是系统沙箱。

## 实际验证与证据

运行时覆盖（本轮读取使用，未复制到工作树）：

```powershell
$env:D1_PI_NODE='C:/Projects/Fucode/Fufucode/runtime/node/node.exe'
$env:D1_PI_CLI='C:/Projects/Fucode/Fufucode/runtime/pi/node_modules/@earendil-works/pi-coding-agent/dist/bundle/cli.js'
$env:D1_OMP_EXE='C:/Users/mio/.bun/bin/omp.exe'
dotnet run --project tests/Fufucode.Core.Tests -- --d1-only
dotnet run --project tests/Fufucode.Core.Tests --no-build -- --d1-regression-only
dotnet build src/Fufucode.App --no-restore
node --check scripts/d1-probe.mjs
node --check scripts/d1-ui-fixture.mjs
git diff --check
```

- 最终真实引擎定向：**28 PASS，exit 0**。本地 HttpListener/SSE 明确选择 `d1/d1-fixture` 合成模型，无云费用；实际 Pi/OMP 进程、RPC、桥接、工具写入和文件恢复均真实执行。包括迁移备份；每引擎创建、对话、单次 UI settled、允许写入/拒绝无写入、停止撤回 followUp、历史文件、排他 lease、关闭恢复身份/历史/草稿、进程 exit 73 导致 pending fault、不重放 prompt 的恢复、非法模型拒绝创建、正常模型继续；另验 OMP 操作前后 Pi 文件 SHA-256 不变、缺失 OMP 无替代引擎/元数据改变、不同引擎 ID 重绑拒绝。
- 最终既有持久化/ExtensionUI 定向回归：**63 PASS，exit 0**；包含信任、迁移备份、审批匹配/过期/取消/断连、旧 generation 不授权新连接。FakePi 工装已构建；不是云模型试跑。
- 最终 App 构建：**0 警告、0 错误，exit 0**；两个 Node 脚本语法检查、diff 格式检查 exit 0。
- 证据绝对根：`C:\Users\mio\.codex\worktrees\f80f\Fufucode\tmp`。日志 `d1-final-tests.log`、`d1-final-regression.log`、`d1-final-build.log`；最终真实会话/metadata/文件根 `d1-tests/c327bf0ef35d41ea911bf8cba2b5270b`。这些隔离数据为本地证据，未夹入 Git。
- 原生 computer-use：运行 `node scripts/d1-ui-fixture.mjs`（上述运行时覆盖），在独立 tmp/d1-ui 数据根/项目中实际点击选择 OMP、新建；输入 WRITE 中文审批验证，确认写入文件在允许前不存在、允许后内容为 D1 native approved；关闭重开恢复 OMP 历史；创建默认 Pi，重复原生允许写入并核验前后文件；修正后再次创建 OMP，截图确认“真实 OMP 会话”。Pi 对应“真实 Pi 会话”。窗口/工装最终 UI EXIT 0。界面证据为本会话工具截图，持久产物 `tmp/d1-ui/project/omp-ui-approved.txt`、`ui-approved.txt` 与隔离数据。中文输入为自动化 literal typing，**不是输入法组合态验收**。
- 云模型最小试跑：首选显式 `devin/swe-2`，OMP 60 秒超时，记录为未通过；随后最低价替代 `magpie/group/auto-qfmodel`，分别运行 `$env:D1_MODEL='magpie/group/auto-qfmodel'; node scripts/d1-probe.mjs --cloud` 和 `node scripts/d1-probe.mjs --pi --cloud`，均 exit 0、真实终止 completed、会话 assistant 文本为 OK。仅读全局认证供试跑，不写认证/凭据到记录。
- 云证据：`tmp/d1-probe/2026-10-10T13-03-53-784Z_01a125e9-adb8-76f9-a1d7-39142090707c.jsonl`（OMP）、`2026-10-10T13-04-25-356Z_01a125ea-290b-7453-ba3d-5305b75dc755.jsonl`（Pi）。免费路由超时记录 `2026-10-10T13-01-03-518Z_01a125e7-149e-77ad-964e-adafc0f9fe4d.jsonl`，中止且无有效文本，不归因本产品。

## 修复与未验证项

- 本轮修复 OMP 命令发现/停止队列格式/完成事件不等价、无效 idle 查询提前结束、重复终止通知、恢复身份混用、缺失后端替换风险。新建命名失败在持久 session writer 建立前处理，避免失败泄漏。
- 原生检查发现 OMP 空状态仍写 Pi、无内容 widget clear 被显示为 RPC JSON；已修正，OMP 空状态重新原生验证。widget clear 不显示的分支经代码核对，未单独强制重现 clear 后再截屏。
- 未运行全仓回归（范围定向足够）；未验证包内 OMP 部署/干净 Windows 安装（D0/R/G3）；生产用户数据迁移只在隔离旧 schema fixture 实跑；原生拒绝/停止、完整 IME/DPI/多版本 OMP 未分别实跑。
- 云调用只验两种真实协议短文本；工具审批/停止/异常恢复使用本地合成模型驱动真实引擎，未宣称云工具长任务或 App 私有云认证配置已验收。
- OMP 图像队列恢复、普通扩展市场/生命周期、OMP 分支、后台唯一宿主/IPC/手机均不在本任务交付内；队列恢复仅对当前统一文本发送路径验证。自定义模型只定义 ID，能力上限遵守引擎，不虚构上下文/价格。

## 交付卡

- 任务ID与软件：D1，Codex desktop 本地执行。
- 工作树/HEAD/起点：`C:\Users\mio\.codex\worktrees\f80f\Fufucode`，detached HEAD；起点 `83045c92eaeff44b2a69ca2d88b4b541cc68f4e0`。
- 本任务提交：`8977a45f2985239a49c4101d1d1f54a6feaf0345`（22 文件，D1 实现、工装、验证/缺陷记录）；随后一个仅本记录的补录提交，记录真实实现 SHA。最终补录提交 SHA 由交付回复和 `git log` 获取，不将尚未产生的自身 SHA 写进正文。
- 改动文件：Pi 后端身份/options；Core coordinator/adapter/metadata/manager；App Abstractions/Integration/ViewModel/新建入口；Core.Tests 定向/回归入口、crash fixture；d1-probe/d1-ui-fixture；本记录与 failure-registry。无继承用户改动，其他任务目录受保护。
- 验证：上述实际命令；真实引擎 28 PASS、定向回归 63 PASS、App 构建零警告/错误、两种低价云回复 OK、原生选择/允许审批/OMP恢复与标识。证据见本记录指定本地根与会话截图。
- 未验证：上述包安装、生产迁移、原生拒绝/停止/IME/DPI、云工具长任务、多版本/OMP分支与扩展；各有范围或实跑限制，不标通过。
- 公共接线：D1 桌面入口已接；M 核对提交/范围，复核 G1 并收编 main。D5/O5 使用本记录的身份/能力与生命周期接口，未交付后台宿主或 OMP 通用插件接口；D0 需决定运行时封装后验证，默认缺失错误已经实现。
- 状态：提交待验收；未自行整合 main、push、回收工作树或开始下一节点。
- 原会话是否仍写代码：否，交付后等待 M 定向修复指令。
