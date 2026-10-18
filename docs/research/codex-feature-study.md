# Codex 功能研究与 Fufucode 差距判断（轨道 H）

> 2026-09-30 深入研究补充见 §6 起：依据本机 Desktop 发行包可读 JS、固定公开源码和 Pi 固定版本协议分析调用链。§1–5 保留为首轮资料记录；其中“Desktop 版本未核实”仅适用于首轮，第二轮版本已核实。第二轮不构建、不运行真实任务，不把静态代码存在写成运行验收。

研究日期：2026-09-30。研究快照：track-h，提交 `7c91386b46f3be3fea0f001879ea7a6a516dc4ba`。只读研究，不代表产品功能已经实现或通过验收。

## 1. 证据口径与结论

优先补足用户对运行状态、发送时机、审批范围和修改结果的理解，再考虑会话分叉、计划模式等新增能力。不要把 Codex 的账号、沙箱、云端服务或 app-server 协议直接搬到 Pi 客户端。

本机 `codex --version` 返回 **codex-cli 0.159.2**；帮助确认的是命令入口及参数，未执行真实模型、恢复、排队、云端或插件安装。Desktop 安装版本未核实，没有进行 UI 走查；Cloud 未登录取证。官方页面描述为“文档声称”，不等于本账号可用。所有下表功能的**首次引入版本均未核实**；“0.159.2 存在入口”只证明当前本机帮助包含该入口，不能当作引入版本。

旧官方 URL 多已跳转到 ChatGPT Learn 共用文档，不能据此假定所有 ChatGPT 功能在每个 Codex 表面都有一致入口。任务书以 `docs/archive/assignments/track-h-codex-study.md` 为准；旧 `docs/track-a-codex-study.md` 的 A 轨归属与写 progress.md 指令不适用于本任务。

## 2. 来源索引

- L1：本机 `codex --version`、`codex --help`，0.159.2。
- L2：本机 `codex fork --help`、`codex queue --help`。
- L3：本机 `codex cloud --help`，列出 exec/status/list/apply/diff，标 experimental。
- L4：本机 `codex mcp --help`、`codex plugin --help`；只读帮助，不调用管理操作。
- O1：[CLI 官方概览](https://learn.chatgpt.com/docs/codex/cli)，关注当前功能与接口边界。
- O2：[官方命令参考](https://learn.chatgpt.com/docs/developer-commands?surface=cli)，包括斜杠命令、diff、statusline；文档可能领先本机版本。
- O3：[审批与安全](https://learn.chatgpt.com/docs/agent-approvals-security)。
- O4：[通知](https://learn.chatgpt.com/docs/notifications)及[高级配置](https://learn.chatgpt.com/docs/config-file/config-advanced)。
- O5：[Cloud 官方概览](https://learn.chatgpt.com/docs/cloud)。
- O6：[IDE 官方概览](https://learn.chatgpt.com/docs/codex/ide)。
- O7：[2026-06-23 Remote 官方文章](https://developers.openai.com/blog/mastering-codex-remote-for-engineering)，区分 steer 与 queued prompt、worktree 和移动控制；发布日期不当作首次发布日。
- O8：[子 Agent 文档](https://learn.chatgpt.com/docs/agent-configuration/subagents)。
- O9：[长任务官方文章](https://developers.openai.com/blog/run-long-horizon-tasks-with-codex)，明确提及 app/CLI/IDE 的原生 plan mode 与 `/plan`。

下文源码位置均相对本工作区；锚点来自当前源码或任务书，未构建、未运行 Fufucode。本报告中的“未见”限定于所引用契约及页面，不能理解为全仓证明不存在。

## 3. 功能面及逐项对照

每行版本栏遵循上面的首次引入版本口径。采用是行为目标，不是复制界面或文字。

| 功能 / 表面 | 入口与行为摘要 / 证据 | 版本取证 | 判断、现状与差距锚点 |
|---|---|---|---|
| 审批与权限 / CLI | `--sandbox` 的 read-only/workspace-write/danger-full-access 与 `--ask-for-approval` 的 on-request/never 分开；`/permissions` 查看边界。[L1,O3] | 0.159.2 帮助；引入未核实 | **改造采用**：明确呈现项目信任、工具审批、OS 隔离三个层次。`src/Fufucode.Core/ProjectTrust.cs`、`ChatSessionCoordinator.cs:218` 已有信任/审批桥关口；`docs/runtime-baseline.md` 明确加载信任不限制工具文件访问。不能把 bridge 标成沙箱。 |
| 自动审批审查 / CLI | `--approve-for-me` 将请求交自动审查并用 workspace-write；帮助另有绕过审批与沙箱参数。[L1] | 0.159.2 入口；引入未核实 | **不采用**（首版）：`docs/AI_DEVELOPMENT_PLAN.md` §1 排除自研沙箱；`ChatSessionCoordinator.cs` 只验证扩展审批桥。没有同等审查服务证据，增加按钮会制造权限承诺。 |
| 会话恢复 / CLI、Desktop | `codex resume`，默认选择器或 `--last`；Desktop 保存聊天可继续，本机只核实 CLI 帮助。[L1,O1] | CLI 0.159.2；Desktop/引入未核实 | **采用**：`PersistentSessionManager.cs:133` 已有 ResumeAsync，`RealSessionHost.cs` 负责绑定；重点增加恢复异常、上次中断和项目路径提示，而非重写持久化。真实恢复验收须按当前进度另做。 |
| 会话分叉 / CLI、Desktop | `codex fork [SESSION_ID]`、`--last`；官方共用命令表有 `/fork`。分叉聊天不自动证明隔离工作目录。[L2,O2] | CLI 0.159.2；引入未核实 | **改造采用**：`PersistentSessionManager.cs` 当前公共入口是 Create/Resume/Rename；计划 §M6 将 fork 留后续。先核对固定 Pi 的分支协议与父子元数据，保留原会话，不用复制历史伪装分叉。 |
| 会话 rollback / CLI | O2 提及 rollback 后 `/copy` 不可用，但本次没有取证 rollback 的稳定用户命令、快捷键或精确截断范围。 | **未核实**（入口/语义/引入版本） | **改造采用**（研究候选）：`SessionHistory.cs` 与计划 §M4 要求保留遗弃分支历史。必须拆开“回到会话节点”和“恢复磁盘文件”；未核实前不派实现。 |
| steer 与排队 / CLI、Desktop、Remote | CLI `codex queue --thread ... --message ...`；官方 Remote 将追加工作排队、将方向纠正 steer，显示待发项。[L2,O7] | CLI 0.159.2；引入未核实 | **采用**：`ChatSessionCoordinator.cs:206,237` 已分 Immediate/Steer/FollowUp，`InputViewModel.cs:101` 已有排队文本，`ChatPage.axaml:456` 已有插入按钮。差距是确认投递时机与队列可理解性，不能把已存在入口再次列为待开发。 |
| 停止与队列恢复 / Desktop 对照 | 本次未实测 Codex 停止是否丢弃队列；其精确行为**未核实**。 | 未核实 | **采用**（Fufucode 自有规则）：`ChatSessionCoordinator.cs:348` 先 clear_queue，再走停止；`InputViewModel.cs` 有 pending restore。强化停止后草稿恢复提示，不能把 Codex 未证实语义作为依据。 |
| 计划模式 / CLI、Desktop、IDE | `/plan`；Desktop 官方功能页展示输入区 Add → Plan mode。用于先讨论多步计划。[O9,O2] | 文档声称；引入未核实 | **改造采用**：计划 §1 单 Pi 内核，`ChatSessionCoordinator.SendPromptAsync` 未见专门 plan mode 参数。只靠提示词不能承诺禁止写入；先用计划草稿与审批策略呈现，再由 A/B 核实工具约束，D 做入口。 |
| 子任务/子 Agent / CLI | 官方文档描述委派工作；CLI `agents` 是浏览共享 daemon 会话入口，不是证明能创建子 Agent 的命令。[O8,L1] | 文档声称；引入未核实 | **不采用**（首版）：计划 §1 排除多 Agent 编排；现有 `ChatSessionCoordinator` 管单连接状态。引入并发需要资源、审批归属和结果聚合设计，不能靠多开 Pi 替代。 |
| 后台运行/daemon / CLI | 默认共享后台 server，`--no-daemon` 可不用它；app-server、remote-control 帮助标 experimental。[L1] | 0.159.2 入口；引入未核实 | **不采用**（首版）：计划 §1 排除后台服务；`ChatSessionCoordinator.CloseAsync`、`PersistentSession.DisposeAsync` 为进程生命周期边界。不能承诺关闭窗口后继续执行。 |
| diff 查看 / CLI、Desktop | `/diff` 文档覆盖 staged/unstaged/untracked；Desktop/Remote 可看变更并留行评论；未走查桌面面板。[O2,O7] | 文档声称；引入未核实 | **改造采用**：`ChatPage.axaml` 当前工具卡片以输入/输出为主，`BlockViewModels.cs` 未见完整 Git diff/还原契约。先提供本次变更清单与外部 diff，再考虑原生行评论；整个工作区 diff 不能直接叫“本次 AI 修改”。 |
| 文件还原 / CLI、Desktop | `codex apply` / `cloud apply` 是应用补丁，不能当 undo；CLI 官方概览建议 Git checkpoint。Desktop 单文件还原的入口与对未提交改动的保护本次**未核实**。[L1,L3,O1] | 还原未核实；apply 0.159.2 | **改造采用**（后续）：计划 §M3 支持真实文件修改，但没有自动快照验收；先明确基线、用户改动、文件删除与二进制的恢复范围，由 A 提恢复契约后 D 接按钮。 |
| 用量 / CLI、Desktop | 官方 `/status`、`/statusline` 展示 context/token/rate limits；账号、模型与表面可影响可用数据。[O2] | 文档声称；引入未核实 | **采用**：`StatusBarViewModel.cs:128` 当前仅累计输入/输出 tokens；空值子字段用 `?? 0`，需避免将未知显示成真实零。`MessageTimeline.cs`、`HistoryMapper.cs` 是累计与历史映射锚点，先明确每轮/会话口径。 |
| 账号限额 / CLI、Desktop | 官方 status 含 rate limits，但本次未读账号、未测 reset 或套餐窗口。[O2] | 文档声称；具体账户未核实 | **改造采用**：`docs/archive/assignments/track-a-m6core.md` 为模型/认证链路，Pi 多供应商不共享 Codex 账号限额。仅有供应商可核实接口时显示额度与来源，否则显示未知；token 使用量不能推算剩余额度。 |
| MCP / CLI | `codex mcp list/get/add/remove/login/logout` 管外部服务器；本次只查帮助。[L4] | 0.159.2 入口；引入未核实 | **改造采用**：计划 §M5 采用 Pi 扩展；`Integration/ExtensionCatalog.cs` 和 `ExtensionsPageViewModel.cs` 是现有入口。若未来需要 MCP，优先由 Pi 扩展适配；不在 C# 增建第二套工具执行与授权内核。 |
| skills/插件 / CLI、Desktop | CLI plugin 管 marketplace 与安装；文档描述技能承载可复用指令。[L4,O1] | 0.159.2 plugin 入口；引入未核实 | **改造采用**：现有 `ExtensionCatalog.cs`、`ExtensionPreferences.cs`、`ExtensionsPageViewModel.cs` 已有本地扩展面。先补来源、启停生效时机、失败原因；计划 §1 不做插件市场，也不承诺 Codex 插件可直接运行。 |
| 通知 / Desktop、CLI | Desktop Settings 配完成/权限/问题通知；CLI `notify` 外部程序，TUI 可按事件和是否失焦过滤。[O4] | 文档声称；引入未核实 | **采用**：`ExtensionUi.cs` 的扩展通知与 OS 通知是不同层；`StatusBarViewModel.cs` 已显示状态。D 可做后台完成/待审批提醒，A 发可靠终态事件；以 agent_settled 而非 agent_end 判完成。 |
| 声音 / CLI、Desktop | CLI 通知方法可用 BEL，由终端处理，不能保证一定发声；Desktop 专门声音设置与音效规则本次**未核实**。[O4] | CLI 文档声称；Desktop/引入未核实 | **改造采用**：作为本地可关闭偏好，仅对完成/待输入等少数状态发声；`AppSettings.cs`、`SettingsPageViewModel.cs` 为配置锚点。本次不建议复刻音效，也不认为扩展 notify 自动有声音。 |
| 斜杠命令 / CLI、Desktop、IDE | 官方命令参考和 CLI 首页展示 `/model`、`/permissions`、`/review` 等；入口为输入 `/`。[O1,O2] | 文档声称；引入未核实 | **改造采用**：`ChatSessionCoordinator.QueryCommandsAsync:91` 获取 Pi 命令；`ChatPage.axaml:403` 已有命令 flyout。统一可搜索入口、区分桌面操作与 Pi 命令，不能把 Codex 命令直接发送给 Pi。CLI 按 `docs/archive/assignments/track-a-cli.md` 做 REPL，不做 TUI。 |
| IDE 集成 | 官方 IDE 面板使用打开文件/选区上下文并查看修改；本次未安装或启动扩展。[O6] | 文档声称；引入未核实 | **改造采用**：计划 §1 排除完整 IDE；优先文件定位、外部编辑器打开与明确附件引用，D 做页面入口；独立 IDE 插件如需实现另设轨道，不能扩张当前 App 任务。 |
| Cloud 任务 | CLI `cloud exec/status/list/diff/apply`；官方 Cloud 页面支持托管工作、审查及跨端继续。[L3,O5] | 0.159.2 experimental；引入未核实 | **不采用**（首版）：计划 §1 Windows 本机优先，远程留后续；`RealSessionHost.cs` 与 `PiChatSessionAdapter.cs` 是本地进程链路。云端需要身份、隔离、网络、文件同步与成本体系，非 UI 接线工作。 |
| worktree 与远程控制 | CLI `--worktree`、`--remote`；Remote 从手机选择主机/分支并审批。[L1,O7] | CLI 0.159.2；引入未核实 | **不采用**（首版）：计划 §1 排除远程 RPC 服务器和多 Agent 编排。会话 fork 不保证文件隔离，未来由新轨道设计 worktree 管理与生命周期，A 只提供会话契约。 |

## 4. 建议排序与交接

价值/成本采用相对 1–5 分，成本越高越难；排序用价值÷成本，并考虑依赖。不是工期承诺，也不意味着 H 有实现授权。

| 顺序 | 建议切片 | 价值 / 成本 / 比值 | 负责轨道与依赖 | 可审查的完成条件 |
|---|---|---|---|---|
| P0-1 | 解释审批、信任与实际隔离边界 | 5 / 1 / 5.0 | D 展示，A 提准确状态；B 复核桥行为 | 失效桥禁止发送；明确拒绝、超时与权限边界，不显示虚构沙箱能力。 |
| P0-2 | 已有插入/排队/停止恢复体验收尾 | 5 / 2 / 2.5 | D，复用 A 状态机；对应 D Wave 1 | 插入与排队有明确时机；停止清队列，恢复文字不覆写当前草稿；断连不伪报投递。 |
| P0-3 | 用量口径与未知值 | 4 / 1 / 4.0 | D 显示，A 对数据语义 | 不把缺失 token 显示成已测零；恢复历史不重复累计；未知额度不估算。 |
| P1-1 | 命令检索和统一入口 | 4 / 2 / 2.0 | D + A 的 CLI 轨；依赖 get_commands | 每个入口标明本地/Pi 来源；未注册命令不能冒充支持；CLI 保留机读输出和退出码。 |
| P1-2 | 后台完成/待审批通知 | 4 / 2 / 2.0 | D，A 提终态事件 | 只有真实 settled 才完成提醒；可关闭、失焦策略、点击回会话；声音独立偏好。 |
| P1-3 | 修改摘要与 diff | 5 / 3 / 1.7 | D 查看，A 提变更范围；Git 能力可另设轨道 | 明确全仓/本次变更范围；含新文件；无修改/获取失败可区分。先查看，不附带还原承诺。 |
| P2-1 | 会话 fork | 4 / 3 / 1.3 | A 协议与元数据、D 入口，B 必要实测 | 原会话不变、父子可追溯；明确共享目录；固定 Pi 版本证明支持后再实现。 |
| P2-2 | 计划草稿与执行切换 | 4 / 3 / 1.3 | A/B 核实约束，D 展示 | 清楚区别讨论计划与受约束执行；不能只改提示词就宣称只读。 |
| P2-3 | 有边界的文件还原 | 5 / 5 / 1.0 | 新轨道研究 Git/快照，A 契约、D 交互 | 确保用户未提交改动不会被混同；支持范围明确，冲突时不给假成功。 |
| 后续 | MCP 适配、IDE 插件 | 3 / 4 / 0.75 | 新轨道，协调 A/B/D | 证明当前 Pi 扩展兼容、鉴权及 UI 边界后才安排。 |
| 暂不排 | 自动审批、daemon、多 Agent、Cloud/Remote | 2 / 5 / 0.4 | 新轨道且需产品重新定范围 | 先有真实需求和架构方案，不挤占 M6/M7 发布。 |

P0-2 依赖已有实现，不能重复投放“新建 steer 按钮”。P0-3 虽比值更高，但运行控制与审批问题影响用户能否安全完成当前任务，因此放在其后。D 额度有限，先现有链路收尾，再投新页面。通知不可绕过桥等待，CLI 不得重写 Core（`docs/archive/assignments/track-a-cli.md`）。

## 5. 未核实项与后续取证边界

- 所有功能首次引入版本、Desktop 当前安装版本、Cloud 实际账号权限：未核实。未把官网截图里的版本或文章日期当发布证据。
- Codex rollback 用户入口和恢复范围、Desktop 单文件还原、专门声音设置：未核实。官方命令参考未找到 `/undo`，这不足以证明所有版本没有该功能。
- Codex 真实 steer 到达点、停止对待发消息的处理、跨进程排队竞争、后台关闭行为：未实测；只记录帮助或文档声称。
- Pi 0.87.1 的 fork/计划工具约束/MCP 兼容：本研究未实测，不新增 RPC 命令假设。固定版本与后续验证以 `docs/runtime-baseline.md` 和 A/B 实测为准。
- Fufucode UI、真实模型、安装/重启路径：本研究未运行，源码存在不代表验收通过。这里只做可追溯差距判断。
- 官方旧 slash-command URL 和部分 `.md` 页面取回失败，改用可读取的官方 developer-commands 页面；未用搜索摘要补造命令。

本报告仅含原创分析和必要接口标识，没有复制产品资产、界面文案或商标素材。协调者可按 P0/P1 切片派发；H 不直接修改产品或他轨文档。

## 6. 第二轮：真实 Desktop 代码证据与边界

本轮按用户要求分三个子研究域：会话与任务状态、修改审查与工作面板、权限与扩展设置；主研究补充长对话、导航与快捷命令。研究对象不再只限于帮助和官网清单。

### 6.1 固定版本与材料

| 材料 | 实际核实值 | 能证明什么 |
|---|---|---|
| Windows 安装包 | `OpenAI.Codex`，AppX `26.928.2636.0` | 本机安装包身份；不等于内部所有组件版本 |
| Desktop 包 | `app/resources/app.asar`，540844015 bytes | 未修改包；通过文件读取、解析 ASAR 索引，按 offset 读取目标 JS；没有解包写出文件 |
| 内部 package.json | `openai-codex-electron`，`26.928.21956`，入口 `.vite/build/early-bootstrap.js` | Desktop 内部应用版本；与 AppX 版本分别记录 |
| 公开 Codex 源码 | `openai/codex`，main 提交 `d42056091aded7feb1d88ac7e83972108b2aa478` | CLI/core/app-server 源码证据；根树没有完整 Desktop 前端源码目录，不能把它称为整个 Desktop 的开源代码 |
| Fufucode | 源码起点 `7c91386b46f3be3fea0f001879ea7a6a516dc4ba`；研究期间 HEAD 为 `f05787ea3babcbf3ac2627d291682bff9705891b` | 两提交差异只含首轮 H 文档，比较用的产品源码相同；本研究没有执行 Git 写操作 |
| Pi | 官方 `v0.87.1`，提交 `f07218c4d4bbc12bef056a7058c3dd49dfe41abe` 的 RPC 命令文档 | 固定版本接口定义；本轮未发送 RPC 验证 |

安装根：`C:\Program Files\WindowsApps\OpenAI.Codex_26.928.2636.0_x64__2p2nqsd0c76g0`。以下 `webview/assets/...` 均是 `app/resources/app.asar` 内部路径，不能当普通磁盘文件链接。JS 是发行包压缩构建产物，不等于有许可可复制的开源 TS/React 项目。本轮只分析行为、接口和状态关系，用原创方案在 C#/Avalonia/Pi 中实现。

针对所读关键前端模块检查 `sourceMappingURL` 对应的 `.map`：thread-virtualizer、virtualized-turn-list、keyboard-shortcuts-settings 的 map 均不在包索引中。因此证据以文件名、稳定字段/调用、必要时 0 基字符偏移定位，不编造源 TS 行号。公开源码以固定提交路径和符号定位。不假定公开 main 与本机 bundled engine 一致。

### 6.2 证据等级

- **D-static**：本机 Desktop JS 的条件、数据结构和调用链直接可读；可证明该构建包含实现，不能证明账号开关启用或运行正常。
- **O-source**：固定公开 app-server/core 源码；可证明源码的协议/处理规则，不能反向证明安装包使用同一 commit。
- **P-doc**：固定 Pi 文档；可设计适配，但须 A/B 做真实 Pi 验证才能列为产品已支持。
- **F-source**：Fufucode 当前源码；存在实现不等于已过 UI/真实模型验收。
- **Proposal**：本报告原创设计建议；下文建议的接口/表/DTO 名字均不是现有接口或 Pi 命令。

## 7. 主研究：长对话、导航与一致操作入口

### 7.1 长对话窗口与稳定滚动锚点 — 采用

**真实机制。** `webview/assets/thread-virtualizer-e1964ca5c039.js` 是独立布局计算模块：`t` 根据 `turnKey`、估计/实测高度、gap 和 pixelScale 得到 top/bottom offsets 与索引；`n` 以 distanceFromBottom/viewportHeight/overscan 求可见范围；`r` 在列表变化时按 anchorKey 重建窗口；`i` 用新旧布局同锚点的 bottom offset 差补偿滚动距离；`a` 计算目标 turn 的居中距离。默认估计高度是构建里的 280px，不应复制成 Avalonia 固定参数。

完整模块 SHA-256：`d1a120db42f0789fa5c7b6381b76090413ac027377844731ab7c4b5ccf3fcfb0`。

`virtualized-turn-list-d5ddce2a9401.js` 使用这个模块，维护测量高度和保留的 turn keys：字符偏移约 7982 为 overscan 计算，约 21071 为 anchorKey/new layout 补偿，约 17041 注册 `getEntryGeometry` 与 `scrollToKey`，约 8588 的两次 requestAnimationFrame 等待初始布局稳定。其 SHA-256：`720d31ffb8bb07f6666efc8ba049316cda38c2a137d70bd0283ab4bf0392cd19`。

**Fufucode 已有与缺口。** `Views/ChatPage.axaml:144` 的 ScrollViewer 和 :153 的 VirtualizingStackPanel 已存在；`TimelineViewModel.cs:126` 有上翻停止自动跟随、:136 回到最新；`ChatPage.axaml.cs:75` 用距底部 32px 判断跟随；`RefreshScheduler.cs` 合并流式刷新。因此不是重新增加“虚拟化/回到最新”按钮。缺口是稳定条目锚点、历史分批加载与高度变化补偿是否可靠，尤其外层 ScrollViewer 是否实际给虚拟面板有限 viewport，须测量后确定，不能凭 XAML 名字认定虚拟化工作。

**移植操作。** D 在现有列表控件上保留条目 ID，不先重写虚拟列表；给每会话保存 `(anchorEntryId, offsetWithinEntry, followLatest)` 的视图状态，布局完成后定位锚点；插入历史、工具卡片展开和代码换行时按锚点恢复。A 负责稳定历史 entryId 与分页数据，UI 不能把列表下标当永久标识。若现有 Avalonia 虚拟容器无法约束 viewport，先修控件组合；只有实际性能证据不足时再开发自定义估计高度窗口。

**必要验收。** 长历史中仅实现有限可见容器；用户读中间条目时新 token/历史前插/展开卡片不回拉；回最新继续跟随；切换会话可恢复锚点；中文文本和代码换行重测后不跳位。不照搬 React DOM、CSS 或 280px 常量。

### 7.2 用户消息导航轨与书签 — 改造采用

**真实机制。** `bookmarked-thread-user-message-navigation-d04b009202ed.js` 的 `C`（字符偏移约 1041）接收 getItems/onBookmarkChange/onPreviewItem/onRevealItem，使用 scroll controller 的 `holdViewport`；Alt+ArrowUp/Down 只在指定会话滚动容器内导航，未挂载目标时走 reveal 回调，等目标布局后再次定位。`R`（约 4794）按 bookmarkKey 合并书签集合，以 turnKey 或 bookmarkId/id 判断；`thread-user-message-navigation-rail-app-2cf45bafb5a1.js` 是导航轨/悬停预览，含预览不可用及未取得 bookmarkId 时禁用操作分支。相关开关控制它是否可用，未核实本机当前启用状态。

**差距。** `SessionHistory.cs` 已保留原始 Id/ParentId/LeafId；App 历史映射生成的展示消息 ID 不能直接视为持久 Pi entryId。`DesktopMetadataStore.cs` schema v2 只有 project/session/draft/preferences，没有条目书签表；当前 Timeline 只有回最新导航。

**移植操作。** A 将 Pi 原始 entryId 作为独立元数据透传，保留现有展示 ID；桌面元数据新增会话作用域书签，不写 Pi JSONL。D 先做“上一条/下一条用户消息＋消息目录”，再加收藏与短预览；从未加载历史跳转时先通过 A 加载目标范围，然后由 Avalonia 列表滚动，不把缺少 entryId 的条目伪造为可持久书签。会话删除、fork 来源及遗弃分支书签需有可解释状态。

**验收。** 历史恢复后书签仍指向原消息；长列表未渲染项可定位；分支变更时不得指向另一个内容相同的消息；导航不抢输入法组合键；用户选择文字期间不强制滚动。优先级 P1，A+D。

### 7.3 统一命令注册、能力门控与快捷键冲突 — 采用

**真实机制。** `keyboard-shortcut-command-order-6f9ce4190ce7.js` 的 `s/c` 根据 commandAccess、platform、browserEnabled、fileLanguageFeaturesEnabled、isAutomationsEnabled 等判断命令可用/可展示，不把键盘入口作为权限旁路。`keyboard-shortcuts-settings-8b35561e41b2.js` 的字符偏移约 4054 检查与 registeredCommands 的冲突；约 4400 分 set/append/replace/reset 更新；约 22071 调 `set-codex-command-keybinding`，成功后刷新 keymap 与有关全局热键状态。不是仅保存一段按键字符串。第一模块 SHA-256：`e313755c22f8b54590b3178eddcab7b6b380c26bbf9604bb0dcbacce63488e22`；设置模块：`ac2a2e325ee4de366e4a906ddd3fbfeaa72a98475d4fd3eedac29f49adf633a9`。

**差距。** Fufucode 现有 ICommand 与 `ChatPage.axaml.cs:108` 键盘处理包含 Enter/Ctrl+Enter、IME 守卫，扩展命令通过 flyout 展示；未见一个同时驱动按钮、搜索菜单与可编辑快捷键的能力注册契约。不能打破既有 IME 组合处理去仿制快捷键。

**移植操作。** D 在 App 建原创 `DesktopCommandDescriptor`（建议名），包含 id/分类/执行委托/CanExecute/可见性/作用域/快捷键；菜单、命令搜索与 KeyBinding 共用实例。A 提示会话能力与操作状态，Pi 扩展命令作为不同来源注册，不能把一个本地命令直接拼成 `/...` 发送。配置复用桌面 preferences，先只做窗口内快捷键；操作系统全局热键是另一阶段。冲突按窗口/输入框/文件面板等作用域判断，不能只比较字符；无能力命令不可通过键盘触发。

**验收。** 同一操作按钮与快捷键可用性一致；修改快捷键后立即应用并重启保留；冲突可定位到另一操作；中文组合期间 Enter/Ctrl+Enter 不误发；文件面板有焦点时不执行聊天专用动作。P1，主要 D，A 提能力快照。

### 7.4 本地 UI 状态与服务端会话状态分层 — 采用

ASAR 同时包含 local Codex、Cloud 与 ChatGPT 共用组件。`task-unread-8c3b9df47d51.js` 字符偏移约 543 调远端 `/wham/tasks/{task_id}/mark_read` 并按账号有效性检查结果；它是 Cloud 阅读状态链，不能推断本地 Pi 会话需调用同一服务。`sidebar-actions` 的部分实现也是 ChatGPT source feedback，不应按文件名误归类为 Codex sidebar 管理。

移植时 A 保持 Pi JSONL 为会话历史唯一来源；`DesktopMetadataStore` 保存置顶、分组、阅读游标、视图锚点、草稿等桌面元数据。未读数量用已收到并可定位的新条目与本地阅读游标计算，勿用 `LastOutcome.Completed` 代替已读。项目/会话作用域、异步返回对应 sessionId、删除后的孤儿元数据清理须明确。当前不接 Cloud 状态服务，也不声称与其他设备同步。


## 8. Pi 能力适配：先复用协议，再补桌面事务

### 8.1 消息分叉与整支复制

固定 [Pi v0.87.1 RPC 定义](https://github.com/earendil-works/pi/blob/f07218c4d4bbc12bef056a7058c3dd49dfe41abe/packages/coding-agent/docs/rpc-commands.md#L604) 已核实 `fork(entryId)`、`clone`、`get_fork_messages`。fork 从当前活动分支的历史用户消息创建新会话，返回原提示词；clone 复制当前活动分支到当前位置。两者都可能被扩展取消，`success:true` 与 `data.cancelled:true` 可以同时出现，不能把 RPC 成功直接显示为分叉成功。`get_fork_messages` 提供合法候选 entryId，而非任意展示消息 ID。

**建议事务，非现有 API：** A 在空闲状态通过候选 entryId 发 fork，检查 cancelled 后查询实际 sessionId/sessionFile，建立子会话元数据与来源 `(sourceSessionId, sourceEntryId)`；原索引继续指向原 JSONL。当前进程可能已切换会话，须重绑 PersistentSession、SessionLease、历史/草稿作用域，再通知 RealSessionHost 与 D 的选中项。不能只向 SQLite 插一条记录，也不能复制屏幕文本冒充 Pi 会话。查询或落库失败应重新核对 Pi 实际会话后提供恢复，避免 UI 指向旧会话却把新消息发入子会话。

D 的消息菜单区分“从这里分叉”和“复制当前分支”，fork 返回的 text 作为可编辑草稿，不自动提交；保留旧草稿并说明来源。未合法映射到 Pi entryId 的消息禁用分叉。

**验收：** 扩展取消不产生假子会话；新会话恢复包含正确历史分支；原会话仍可独立恢复；并发运行时不给出可能竞态的分叉入口；RPC 后元数据写入失败可恢复一致性；失败不丢草稿。本次仅核实协议与源码差距，未运行真实 fork/clone。

### 8.2 上下文使用量与压缩

[同一固定协议的 get_session_stats](https://github.com/earendil-works/pi/blob/f07218c4d4bbc12bef056a7058c3dd49dfe41abe/packages/coding-agent/docs/rpc-commands.md#L520) 区分历史 token/cost 与当前 context；不要用累计输入输出 token 除以模型窗口来推算当前上下文百分比。A 为统计查询加明确 DTO 与未知值，切换会话、模型或压缩后重新取值，异步结果按 sessionId/generation 校验；D 将累计成本、当前上下文与供应商账号额度分别呈现。Pi 上下文统计不提供 ChatGPT/Codex 账号额度。

压缩按钮调用固定版本 `compact`，须跟踪实际完成/失败与历史更新，不把请求接受当完成。保留压缩前历史的查询语义，书签仍引用稳定 entryId；压缩期间与发送的互斥条件由 A 的能力快照决定。上下文缺失显示未知，失败给出原始原因，不能填 0%。验收用真实长会话比较压缩前后统计、切换中迟到响应和失败恢复；本研究未执行这些产品验收。


## 9. 子研究：权限、扩展、通知与长期任务

### 9.1 审批范围与模式解耦 — 改造采用

**证据。** 固定公开源码 [permissions.rs](https://github.com/openai/codex/blob/d42056091aded7feb1d88ac7e83972108b2aa478/codex-rs/app-server-protocol/src/protocol/v2/permissions.rs#L789) 的 `PermissionGrantScope` 只有 Turn/Session，默认 Turn；权限请求关联 threadId/turnId/itemId。这里指该权限授予类型，不表示所有审批响应都只有这两个枚举。发行包 `permissions-mode-dropdown-d57f33b74389.js` 在字符偏移约 14108/17809/21258 有 requirements、Guardian gate 与 `updateThreadPermissions` 通路。未读本机 gate 值，不能断言当前显示全部模式。

公开 [thread.rs](https://github.com/openai/codex/blob/d42056091aded7feb1d88ac7e83972108b2aa478/codex-rs/app-server-protocol/src/protocol/v2/thread.rs#L279) 将 collaborationMode 与 permissions 分开。计划模式属于行为配置；其标签本身不能证明只读或沙箱隔离。

**差距与移植。** Fufucode 已有 `ChatSessionCoordinator` 的桥接可用性检查和扩展 UI 回复，`extensions/fufucode-bridge/index.ts`/`policy.ts` 将批准绑定到单次请求参数快照。优先保留按请求批准；以后若加入记住授权，应由 A/B 定义作用域与失效条件，D 明示本轮/本会话。计划模式另行制定写入与命令策略，并通过 Pi 扩展工具边界执行；不能单靠 prompt 承诺只读，更不能把审批桥描述为 OS 沙箱。

**验收。** 参数变化、拒绝、超时、断线和桥接缺失仍闭锁；会话切换不继承旧审批；计划阶段若承诺禁止写入，实际写文件与执行命令必须受约束。研究未运行这些行为测试。

### 9.2 自动化与目标引擎是两条链 — 后续独立立项

**D-static。** `automations-page-5f2fc3cbb321.js` 包含 canCreateLocalAutomations/canCreateCloudAutomations（偏移约 50490/50518），创建/更新/删除/RUN_NOW 动作（60629/62898/64145/65648）。`local-conversation-heartbeat-automation-7d88834157e7.js` 有 nextRunAt、暂停/恢复，约 4022 把 automation ID/scope 交给动作处理器。这只证明 UI 和宿主动作路径存在，未核实调度器运行、账号开关或重复触发处理。公开协议中的 ScheduledTaskSummary 是插件计划任务元数据，不能拿来充当 Desktop 自动化 CRUD API。

**O-source。** [thread_goal_processor.rs](https://github.com/openai/codex/blob/d42056091aded7feb1d88ac7e83972108b2aa478/codex-rs/app-server/src/request_processors/thread_goal_processor.rs#L126) 处理 thread_goal_set/get/clear、state DB 与功能开关；[goal extension](https://github.com/openai/codex/blob/d42056091aded7feb1d88ac7e83972108b2aa478/codex-rs/ext/goal/src/extension.rs#L143) 和 [accounting.rs](https://github.com/openai/codex/blob/d42056091aded7feb1d88ac7e83972108b2aa478/codex-rs/ext/goal/src/accounting.rs#L255) 分别处理运行/续跑与 token accounting。持久化目标及预算机制不等于定时任务。

**移植。** Fufucode 首版退出结束 Pi 子进程，不能先做“定时”界面却没有可靠执行宿主。后续轨道分别设计：调度任务保存时区/下一次运行/状态/运行记录/幂等执行键；目标保存客观完成条件、预算、暂停/恢复与终态，续跑复用官方 Pi 进程。需决定关闭 UI 后由谁运行、登录锁定时如何处理、崩溃如何恢复。不得依赖 Codex 私有自动化接口，也不将 Goals 默认开关外推到本机安装包。验收覆盖暂停/删除/run-now、重启补跑策略、重复触发、预算耗尽和真实完成条件。

### 9.3 通知策略先于声音选择 — 改造采用

`notifications-settings-a6a254747182.js` 偏移约 5680/5727/6259 有完成通知 off/未聚焦/始终策略；10806/11362/11697 涉及声音导入、试听和设置。子研究未定位完整的 turn settled → 原生通知/声音触发链；`notifications-runtime` 中账户通知不能当作此链证据。因此只确认设置界面，触发时机未核实。

移植由 A 提供基于 agent_settled 的完成、失败、审批等待事件；D 做按焦点策略与跳转到所属会话；通知以 `(sessionId, generation, terminalEvent)` 去重。完成和等待审批是不同事件，不能让等待用户操作显示为成功；声音可在基础系统通知稳定后补充。验收成功/失败/审批等待、窗口聚焦抑制、事件重放只发一次、点击进入正确会话与关闭偏好生效。

### 9.4 模型、扩展和 MCP — 复用现有边界

`ChatSessionCoordinator.cs:73–88` 已查询模型、设置模型/思考等级，`AppSettings.cs` 有偏好，`PiChatSessionAdapter.cs:243–250` 有 usage 映射；不重新实现 Codex 账号体系。`SessionUiWiring`、`ExtensionCatalog`、`ExtensionsPageViewModel` 已接 Pi 扩展。`get_commands` 是扩展命令/技能入口，不能当作 MCP 工具协商。

A 提供能力与应用状态，D 明确“已保存偏好/已应用当前会话/需要重启”；B 验证启停、加载失败和扩展 UI 超时。若独立立项 MCP，则增加协议客户端、连接状态与凭据存储，再决定由 Pi 扩展还是宿主暴露工具；插件列表不能冒充实际可用 MCP 工具。此项保持本项目 Pi 扩展路线。


## 10. 子研究：修改审查、文件成果与终端

### 10.1 Diff 是结构化工作面板 — 采用

**D-static 证据。** `code-diff-e4a1b1663a6c.js` 的 stageFile/unstageFile/revertFile/revertHunk 位于 0 基字符偏移约 7434/7638/7830/8427；openFile 约 16196 连接外部编辑器入口。它把操作划分到文件与 hunk，不只是渲染一段工具输出。`use-diff-annotations-36f450d646e7.js` 约 12840 的评论锚含 filePath/workspaceRoot/lineNumber/startSide/localDiffHunk，约 15684 有 replyToReviewThreadId。注释关联的是文件、侧别、hunk 和回复关系。

**O-source 证据。** [review.rs](https://github.com/openai/codex/blob/d42056091aded7feb1d88ac7e83972108b2aa478/codex-rs/app-server-protocol/src/protocol/v2/review.rs#L45) 区分工作树、分支与提交审查；工作树语义包含 staged/unstaged/untracked。这与“某次工具返回了一段 diff”不同。

**F-source 差距。** 当前 InputViewModel → PiChatSessionAdapter → ChatSessionCoordinator → PiRpcClient 处理纯文本发送与 Pi 事件；ChatPage.axaml:264–292 的工具卡是折叠 TextBlock 输出，BlockViewModels.cs:92 起只做工具状态/输出折叠。没有结构化 Git 变更 provider 或审查面板。不能把 Git 查询塞进 Pi stdout/event pump，阻塞模型流。

**移植步骤。** A 提供独立只读 Git provider，返回 workspaceRoot/base/scope、文件状态、rename/binary/untracked 和 hunks；查询结果含版本标识，D 做文件列表、两侧/统一 diff 及跳转。首片默认查看当前工作树，若声称“本轮改动”，必须捕获轮次起点或明确基线；不能把用户原有改动算作 Agent 本轮产出。先采用成熟 Avalonia 文本控件与 diff 数据模型，未测性能前不承诺大文件完整内嵌。

**验收。** 非 Git 目录给明确结果；分支/提交/工作树范围一致；rename、binary、untracked 和读取失败可区分；切项目时迟到查询不污染当前列表；用户已有改动不被错误归因。行评论后续使用 `(projectId, path, baseRevision, side, line/hunkFingerprint)`，版本变化显示过期，不静默绑定到别的行。

### 10.2 对话回滚与磁盘恢复必须分开 — 改造采用

公开 [thread.rs 的 ThreadRevertParams](https://github.com/openai/codex/blob/d42056091aded7feb1d88ac7e83972108b2aa478/codex-rs/app-server-protocol/src/protocol/v2/thread.rs#L1259) 明确：回滚更改已保存的对话历史，不撤销本地文件改动。发行包 Git 文件/hunk revert 是另一条链。Fufucode `InputViewModel.ApplyPendingRestore` 恢复排队文本，PersistentSessionManager.ResumeAsync 通过 get_entries 恢复历史；两者都不能承诺磁盘恢复。

文件/hunk stage、unstage、restore 作为第二片由 A 的 Git service 与 D 操作视图完成，执行前提供具体目标与范围，执行时重新核对基线/文件内容。用户先前未提交的修改、文件同时被编辑及 hunk 位置变化时不直接覆盖；先做只读 diff 再立项磁盘恢复。对话回滚另需核实 Pi 固定协议与可恢复边界，不照搬 Codex thread/revert 命令。

### 10.3 编辑器与成果标签绑定所属会话 — 改造采用

`text-file-editor-tab-content.electron-bfdfb0e80411.js` 约 2290 引入 pierre-file-editor，约 52661 接 currentFilePath/onChange/openFileLink/onSave/originHostId，约 70397 处理未保存内容丢弃生命周期。`artifact-session-tab-content.electron-fb7ec55b5bc6.js` 约 10290 调 artifactSessions.open({artifactRef, threadId}, …)；`artifact-session-binding-85ce8bf62d8d.js` 约 623/869 核对文档上下文和 threadId，失配拒绝继续。`unrestored-thread-tab-route` 提供无法恢复的兜底。restoreImagePreviewTab 属于标签预览恢复，不能当作聊天或磁盘回滚。

现有 DesktopMetadataStore 只有项目/会话/草稿/偏好，没有 tab/artifact 模型；OpenFolderAsync 交给系统 shell，不是内置文件浏览器。D 后续增加会话工作面板和有限文件编辑器，A 提供宿主读取/保存契约。原生标签模型建议包含 projectId/sessionId/canonicalPath/panelKind，dirty 内容与已保存文件分开。关闭和切会话处理未保存修改；写入前检测磁盘外部变化；删除会话/失效路径恢复为可解释兜底。artifactRef 不必复制 Codex 私有形态，按本地稳定 ID 与来源关系设计。

**验收。** 保存/取消/磁盘外改/关闭 dirty 标签可预测；同名文件跨项目不串；重启可恢复有效标签；失效文件和会话不偷偷挂到其他任务。仅重用交互原理，不能直接复制 Desktop React 组件到 Avalonia。

### 10.4 终端是独立 PTY 会话 — 后续切片

`terminal-tab-2c37640cd66b.js` 引入 terminal-panel；`.vite/build/main-DkWgQSQe.js` 约 905026 创建 node-pty backend，约 3077818 起 manager 管 create/attach，按 window/session 保存 conversationId、host/environment/cwd，缓存约 16K 字符；attach 校验 thread/host/environment 变化并拒绝错目标。这证明静态生命周期机制，未实测本机终端 UI/gate。

Fufucode 若加入真正终端，由新轨或 A 提供 ConPTY/PTY service，契约为 create/attach/write/resize/terminate 和独立 terminalSessionId，D 使用成熟 Avalonia 终端组件。绑定 project cwd 与 Pi 会话，但不能把用户终端输入发进 Pi JSONL；审批执行日志也不等于可交互终端。选择缓存/回放上限、进程退出和重连策略；关闭标签是否结束 shell 必须明确。验收中文输入与编码、尺寸变化、断连/重附着、cwd 正确、会话切换不误输入、进程结束状态及输出量压力。Codex 的约 16K 缓存只是观察值，不作本产品标准。

### 10.5 浏览器面板 — 待核实，暂不投放

发行包主进程有 browserHost/browserTabs 与 cloud-browser side-panel 资源，但子研究没有确认它们属于本地 Codex 工作区面板或当前 gate。不能据文件名承诺移植浏览器控制器。先确认实际产品需求、执行环境和可用控制协议，再开独立研究轨。


## 11. 子研究：会话生命周期、状态投影与投递

下列 Desktop 锚点属于 `webview/assets/app-shared-a906948d8868.js`，大小 7,713,650 bytes，SHA-256 `50c38d92886db2b9832ec21932ca80fd0d997e4c3cc716c4139e9085c4f15d25`。符号为压缩产物符号，非稳定公开 API；用符号及附近协议字符串定位，不把偏移当 TS 行号。未实测 UI/gate，公开 Rust 与安装包不视为同一源码版本。

### 11.1 Thread、Turn、提交和恢复分开建模 — 采用

Desktop `F5t` 处理 thread/status/changed，更新 threadRuntimeStatus；`l7t` 处理 turn/started、turn/completed，更新对应 turn；`N0t` 投影 lastTurnStatus、hasPendingTurnSubmission、threadRuntimeStatus；`C6t/T6t/w6t` 汇总 inProgress/activeTurnId，纳入未结束 submission、resumeState、ThreadStatus 与最新 TurnStatus。UI 的待提交状态不等于服务端已实际执行。

公开 [thread.rs:1645](https://github.com/openai/codex/blob/d42056091aded7feb1d88ac7e83972108b2aa478/codex-rs/app-server-protocol/src/protocol/v2/thread.rs#L1649) 的 ThreadStatus 为 notLoaded/idle/systemError/active{activeFlags}；[turn.rs:30](https://github.com/openai/codex/blob/d42056091aded7feb1d88ac7e83972108b2aa478/codex-rs/app-server-protocol/src/protocol/v2/turn.rs#L30) 的 TurnStatus 为 completed/interrupted/failed/inProgress。两套枚举各回答不同问题。

Fufucode `ChatSessionCoordinator.cs:7–17` 已有 Connection/Activity/LastResult 三轴，保留并补独立恢复进度与未确认提交状态。A 输出能力快照，D 按实际状态决定发送、停止、分叉、重试可用性；不能用一个 Running/Idle 标签推导所有操作。验收连接失败、提交未确认、等待审批、停止中、运行终结和历史装载状态互不伪装；上一 generation 的迟到结果不改变当前操作能力。

### 11.2 可编辑队列：借鉴归属设计，保留 Pi 边界 — 改造采用

Desktop queue controller 发 thread/queue/add、update、delete、reorder，监听 thread/queue/changed 后 reload；空闲 resume 调 thread/queue/start，sendNow 在流式阶段先 steer 再删除 queuedSubmission。其队列由服务端持有，不能照搬为 Pi 已支持的队列编辑 API。

Fufucode `QueuedTexts` 与停止后 `_recoverableQueue` 当前是客户端内存投影；clear_queue 用于从 Pi 清出待投递内容。移植 A/D 应至少区分：本地未发送草稿、Pi 已确认接收的 steer/followUp、投递结果未知、停止后可恢复文本。没有稳定队列 ID 和读取/更新协议时，编辑只作用于本地未投递项；不得将已送入 Pi 的文本静默删除再重发，也不声称跨重启持久队列。若提供本地持久队列，需要单独的投递记录与去重/未知态策略。

验收停止恢复不丢文本、不重复投递；超时不可自动重发；UI 删除不虚构已经取消 Pi 内部队列；切会话后恢复文本归属正确。暂不扩展 Pi 协议去仿造 Codex queue 命令。

### 11.3 Steer 的目标轮次必须明确 — 改造采用

Desktop `von` 携 clientUserMessageId 发 turn/steer；`yon` 要求最新 TurnStatus 为 inProgress，缺 turnId 时等待状态更新，最多约 30 秒，目标轮结束或超时即失败。公开 [turn_processor.rs:1024](https://github.com/openai/codex/blob/d42056091aded7feb1d88ac7e83972108b2aa478/codex-rs/app-server/src/request_processors/turn_processor.rs#L1024) 校验 expectedTurnId 并将请求关联该 turn。

Fufucode 已由 coordinator 命令门串行使用 Pi 的 steer/followUp 投递语义，ack 与 agent_settled 分离。不要复制 Codex expectedTurnId 到 Pi 命令；A 可用 generation/本地运行 ID 管 UI 关联和活动状态重验。D 显示“已提交插入请求”和实际处理结果的区别，轮次结束时处理竞态，未知结果不自动重发。约 30 秒是观察到的 Desktop 参数，不作为 Pi 的协议保证。

### 11.4 恢复运行时与装载历史是两个阶段 — 采用

Desktop `dtn` 先置 resumeState=resuming，thread/read 获取 metadata/workspace；`ltn` 调 thread/resume，合并 live thread 后置 resumed；`Yen` 通过 thread/timeline/list 或 thread/turns/list + thread/items/list 分页取历史。失败回 needs_resume。公开 [thread.rs:1671](https://github.com/openai/codex/blob/d42056091aded7feb1d88ac7e83972108b2aa478/codex-rs/app-server-protocol/src/protocol/v2/thread.rs#L1671) 对 read(includeTurns) 的分页历史使用标为弃用；[thread_processor.rs:2824](https://github.com/openai/codex/blob/d42056091aded7feb1d88ac7e83972108b2aa478/codex-rs/app-server/src/request_processors/thread_processor.rs#L2824) 与 :3641 起分别处理 read/resume。

Fufucode PersistentSessionManager.ResumeAsync:133–140、OpenAsync:143–176 重开 Pi 并核对会话文件/ID。A 将运行时就绪和历史装载完成分开暴露，D 在加载旧会话时保留身份、明确失败原因和重试入口。历史以 Pi get_entries 和 SessionHistory 为准，不把 Codex 分页 API 当作 Pi 已有能力；大历史优化应先决定客户端分页或固定 Pi 协议是否支持相应范围查询。验收历史装载失败不伪造空白新会话，断线重开仍正确关联会话与目录，失败不丢草稿。

### 11.5 Fork 先建立新身份，再恢复视图 — 采用生命周期设计

Desktop 流程为 thread/fork → 得到新 threadId → `zen` upsert conversation 与 copyThreadMetadata → 可选 thread/inject_items → `Len` 恢复/放置新会话 → 增加 fork 来源展示。公开 [thread.rs:544](https://github.com/openai/codex/blob/d42056091aded7feb1d88ac7e83972108b2aa478/codex-rs/app-server-protocol/src/protocol/v2/thread.rs#L544) 的 fork 参数含 lastTurnId/beforeTurnId、cwd、模型/权限覆盖、excludeTurns；[thread_processor.rs:4858](https://github.com/openai/codex/blob/d42056091aded7feb1d88ac7e83972108b2aa478/codex-rs/app-server/src/request_processors/thread_processor.rs#L4858) 校验互斥边界并准备持久化历史。

Fufucode 采用 §8.1 的 Pi fork/clone 事务；复制合适的桌面偏好但不复制审批权限，显式保存来源和边界。来源说明属于 UI 元数据，不随意注入 Pi 模型上下文。Codex 的覆盖参数/注入接口不视为 Pi 能力。

**此域未核实：** worktree 创建/挂载具体链路未追完。可借鉴任务与工作目录显式绑定的原则，但本报告不宣称已完整复原 Codex worktree manager。


## 12. 给 Fufucode 的可投放移植路线

最值得学的是稳定的状态模型、结构化工作成果和完整的交互生命周期。技术栈保持 Avalonia + 官方 Pi RPC；Desktop React/Electron 代码用于理解机制，不能直接当成 C# 组件移植。以下为建议任务拆分，未授权 H 编写产品代码，也未替协调者调整当前 M6/M7 排期。

| 建议顺序 | 可独立交付的增量 | 修改责任/落点 | 完成条件与依赖 |
|---|---|---|---|
| 1 | 补全发送/停止/恢复状态和队列归属 | A：ChatSessionCoordinator/PersistentSessionManager；D：Input/Status/History 视图；B：行为验证 | 用户能分辨未确认提交、运行中、恢复中、失败与恢复文本；不重复投递；复用已有三轴状态，不重写 Pi event pump |
| 2 | 长对话锚点与用户消息导航 | A：稳定 entryId 映射；D：Timeline/ChatPage 滚动与导航 | 历史加载/卡片展开/流式更新不拉走阅读位置；原生虚拟化是否有效须实测；之后再加持久书签 |
| 3 | 统一命令表与可编辑快捷键 | D：命令入口、KeyBinding 和偏好；A：能力快照/Pi 命令目录 | 按钮、菜单、键盘 CanExecute 一致，焦点/IME 不误触，冲突可解释；不把宿主命令拼成 Pi slash 文本 |
| 4 | 只读变更列表与 Diff | A：独立 Git provider；D：工作面板 | working tree/base/commit 范围准确，binary/rename/untracked 明确；先建立查看价值，再增加 stage/restore |
| 5 | Pi fork/clone + 来源/草稿恢复 | A：新会话身份/元数据/lease；D：入口和视图重绑 | 依赖候选 entryId 与真实 Pi 验证；扩展取消和中途落库失败可恢复；原会话仍可打开 |
| 6 | 文件/成果标签页 | A 或独立服务轨：文件读写、冲突检测；D：有限编辑器与 tab 生命周期 | project/session/path 身份稳定，dirty/外改/失效恢复可解释；不追求首片变成完整 IDE |
| 7 | 当前上下文统计 + 最终通知 | A：stats/最终状态事件；D：显示、焦点策略与跳转 | 累计 token、当前上下文、账号额度不混淆；最终通知只发一次；先系统通知再自定义声音 |
| 后续 | 真正 PTY、多 worktree、后台调度/目标预算、MCP | 需协调者新增专门轨道 | 先明确宿主、生命周期、协议、持久化与验证；worktree/浏览器完整链路尚未核实，不能靠 UI 资源承诺支持 |

**需要保留的架构界线：** Pi 负责 Agent 协议/模型运行与会话 JSONL；Core 负责会话事务、能力状态与宿主服务；DesktopMetadataStore 保存桌面元数据；App 负责原生视图与交互。Git、文件编辑器、PTY 各有独立服务契约，避免把成果处理、用户终端输入或私有 Codex API 塞进 Pi RPC。

**交付完整性。** 第二轮覆盖长对话/导航/命令、Pi 分叉与统计、权限/计划/扩展/通知/自动化/目标、Diff/文件恢复/编辑器/成果标签/PTY、会话状态/队列/steer/恢复。每域包含静态证据、Fufucode 差距、迁移步骤与待执行验收。未核实 worktree manager、浏览器面板归属、调度器和通知完整触发链、账号开关及真实运行效果；未复制 Desktop 资产/组件、未构建或修改产品、未执行 commit/merge/push。


## 13. 第三轮：前端页面设计与原生页面方案

本轮按用户“再具体研究一下前端页面设计”要求，进一步拆解布局、视觉层级与交互状态。沿用三个 Luna/max 子研究，主研究检查 CSS token、作用域/覆盖与分栏，分别研究侧栏/首页、聊天/工作面板、设置/浮层。仅分析安装包 JS/CSS 与本项目 XAML，没有用屏幕截图或实际 UI 走查；下面的“声明尺寸”不是运行时测量，示意图为 Fufucode 原创建议，不是 Codex 截图。

### 13.1 可以确认的布局与视觉基线

主要 CSS 为 `app-shared-342447930c78.css`（SHA-256 `916d8fa55c82aedbf6ca0f53eec06274ec9eafe08397bb7e9e495d089781f93d`）；以下偏移为 UTF-8 解码后的 0 基 Unicode code-point 偏移，不是 ASAR 字节偏移、UTF-16 index 或 TS 行号。

| 项目 | 静态声明/作用域 | 页面设计含义 | Fufucode 的建议 |
|---|---|---|---|
| 间距系统 | :root spacing=.25rem；常见组件取其倍数 | 控件、行与区域使用一致节奏 | 沿用原生 token，采用 4/8/12/16/24 的布局刻度，例外尺寸有明确用途 |
| 侧栏 | offset约113700：clamp(240px, preferred 275px, min(520px, viewport减320px)) | 有偏好宽度、上下界，并给主区域留空间 | 当前 MainWindow 固定260；改为可折叠/可调宽并持久化偏好，窄窗口优先收起而非无限压缩 |
| 对话列 | offset约1216975：browser/chrome-extension/electron body 将 thread-content-max-width 设48rem；wide Markdown 为56rem | 阅读列与宽代码/表格内容有不同边界 | 正文列建议约720–800 DIP；宽成果可进入右面板或独立横向滚动，不让整页左右抖动 |
| 排版基线 | shared :root base14px；electron共用body以base作为 UI size，code size以sm为来源 | UI 字体与代码字体分别配置 | 中文正文建议14–15 DIP并实测，次要描述12–13；代码独立等宽字体。不能把 shared 全局 sm12、其他scope sm13或14 混称唯一运行值 |
| 导航行 | desktop共用body：paddingY=spacing*1.25，行高=base*1.5+2*paddingY | 密度来源于字高和内边距，不是任意固定高度 | 侧栏行约32–36 DIP、触达与键盘焦点完整；多行描述不挤进单行 |
| 内容边距 | desktop共用body panelBase=spacing*5；toolbar token通常spacing*4 | 页面级20、工具栏级16的留白分工 | 页面/控件/列表三个层级分开，不给每层容器重复24px padding |
| 对话节奏 | root conversation-item-gap16px、grouped-item-gap4px | 一个任务过程内紧凑，语义段落之间明显分组 | 工具过程/思考归为当前轮组；用户新请求与最终回答之间留清晰段落间距 |
| 圆角与颜色 | 圆角有base与scale；前景/次要文字/表面/hover/边框按语义token引用，部分字体/主题可运行覆盖 | 视觉一致性来自系统与状态，不依赖每张卡片加重边框 | 复用 FufucodeTheme 已有 AppText/AppSurface/AppHover，减少同层容器的边框套边框；强调色用于主要动作与需要注意的状态 |

特别注意：`app-initial` 中40rem的定义属于 extension Markdown scope；shared root 的none也会被 Desktop body 48rem覆盖。读CSS必须同时核对selector、级联、继承和组件内覆盖。没有运行 computed style，不声称桌面屏幕上实测宽度是768px，也不把浏览器专属 compact 模式当Desktop保证。

### 13.2 分栏、窄窗与浮层的真实设计机制

`split-view-ea893f73a28a.js`（SHA-256 `5f26066e2950bbf580544b8373f8a14f266368943198cf68658ab1be43974cf4`）接受 left/right、minLeftWidth/minRightWidth、handleWidth、drawerBreakpoint/MaxWidth/VisibleInset 及开关回调。默认值尾部定义为最小边160、分割交互宽12、抽屉断点720、默认左右比例.5；调用者可以覆盖，不是所有页面统一两栏各160px。

分割条具有 separator role、当前/最小/最大值和focus-visible；布局按 containerWidth 限幅，宽度状态按id保留，窄布局右面板平移成drawer。抽屉计算会约束到容器和visibleInset，上限计算用容器比例.9。右面板有独立宽度/translate变量，避免只用CSS隐藏留下错误占位。

**原生建议。** D 采用 Grid + GridSplitter 管主对话/成果区域，窗口尺寸判断决定并排或抽屉/全页面板；侧栏单独拥有可折叠状态。分割手柄视觉可以细，但命中区需足够宽；键盘也能调整/关闭，持久化后重开仍先按新窗口限幅。不能机械复制CSS px与Electron transform。

宽布局建议三个职责区域：左侧寻找任务，中部阅读/发送，右侧操作文件成果。右侧关闭时正文仍居中并保持阅读锚点；打开右侧时不要缩小文本字号。若空间不足，先将工作面板切为抽屉/专页，再收起侧栏；具体断点建议按各区域最小可用宽度求和，在D实际验证，不照搬720。

### 13.3 Fufucode 现有页面的层级问题与重排建议

当前 `Views/MainWindow.axaml:29` 使用260/*两列，默认窗1200×780、最小800×560；`Views/ChatPage.axaml:27` 页头至少52高，右侧同时可显示连接、模型、审批、队列、扩展、结果、usage和活动状态；:149 Timeline MaxWidth880，:348 composer同为880；`FufucodeTheme.axaml:350–360` card与innerWell都有边框，:364–370 chip也有边框，代码/思考再各自加边框。

这些是源码结构事实；“视觉拥挤/嵌套过重”是据此作出的设计判断，尚无截图验收。改进优先级应是信息层级与布局重排，而非再叠加插画、渐变或更多卡片。

| 区域 | 建议的主要信息 | 收入次要入口的信息 | 必须仍直接可见的异常 |
|---|---|---|---|
| 会话页头 | 标题、项目/分支上下文、当前活动状态、面板开关 | 重命名/扩展数量/历史结果/累计usage进入菜单或详情 | 连接不可用、审批桥缺失等会阻止发送的状态 |
| 时间线 | 用户请求、助手正文与最终结论 | 思考过程默认紧凑；工具过程一行摘要并可展开 | 工具失败或待审批；不能与成功过程一样全部折叠隐藏 |
| composer上沿 | 本会话待处理队列/可恢复草稿 | 多项显示计数与展开列表，不挤满输入区 | 已投递与本地草稿的区别、恢复失败原因 |
| composer正文 | 清晰文本输入、附件/命令入口 | 模型/思考/权限为紧凑选择器或一组配置入口 | 不能发送的原因明确靠近发送按钮 |
| composer右下 | 当前可执行的主动作 | 更多发送方式进入有解释的菜单或分段选择 | Stop、Steer、FollowUp名称与可用时机不能模糊 |
| 工作面板 | 当前文件、Diff范围、编辑dirty状态 | 不相关配置移出面板；面板级更多动作就地收纳 | 未保存/磁盘外改/路径失效直接在当前文档显示 |

不把所有状态都变成同权重彩色chip。正常连接可以弱化为小点/文字；失败、待审批和未知投递保持高辨识度。hover动作需同时在focus-within时出现，键盘用户可找到；重要操作不能只有hover才存在。

### 13.4 原创页面线框（设计建议）

下图仅表达区域职责与层级，具体组件/尺寸需D原生实现与用户评审。

```text
宽窗口
┌─────────────────┬─────────────────────────────────┬───────────────────────┐
│ 搜索 / 新会话   │ 会话标题 · 项目/分支 · 运行状态 │ 文件标签 / Diff / …   │
│                 ├─────────────────────────────────┼───────────────────────┤
│ 项目            │ 用户请求（弱背景、可选中文本）   │ 文件/变更列表与正文   │
│   会话A ·状态   │                                 │                       │
│   会话B         │ 工具过程摘要 ›                   │ 本面板独立滚动        │
│                 │ 助手正文（主要阅读列）           │                       │
│ 最近/置顶       │ 最终结论与成果入口               │                       │
│                 │                                 │                       │
│                 ├─────────────────────────────────┤                       │
│                 │ 队列/恢复提示（有内容才占位）    │                       │
│                 │ 输入文本                         │                       │
│ 扩展 / 设置     │ 命令 · 模型/行为选项       主动作 │ 保存/范围动作就地显示 │
└─────────────────┴─────────────────────────────────┴───────────────────────┘
窄窗口：会话页头保留侧栏/面板开关；侧栏与成果通过抽屉或专页进入，
正文和composer共享阅读宽度；主动作与失败原因仍可见，不仅靠Tooltip。
```

空态、运行态、待审批、失败、恢复中与有成果的布局详见本轮子研究后续小节。线框不承诺尚未立项的终端/编辑器能力，M6可以先用现有聊天/扩展/设置实现同样层级整理。


### 13.5 页面状态设计矩阵（Fufucode 建议）

同一页面的不同状态应复用布局，变化由有限状态控制；不要用一套“无内容大插画”覆盖所有情况。

| 状态 | 主要区域 | 主动作/反馈 | 避免的歧义 |
|---|---|---|---|
| 尚未选择项目 | 简短项目入口与任务输入说明；按真实可用流程决定是否开放composer | 选择项目；配置缺失时给就地入口 | 把“缺项目”显示成“网络错误”或空白聊天 |
| 项目已选、空会话 | 轻量标题/提示；composer成为焦点，少量可执行建议 | 输入任务；建议点击只填草稿或明确立即发送行为 | 装饰插画抢过输入焦点，建议看似可用却没有实现 |
| 正在恢复 | 保持原会话标题和项目；历史装载骨架只占相应区域 | 分别显示运行时准备/历史装载；失败可重试 | 先闪空会话，失败后静默新建导致身份错乱 |
| 正在运行 | 正文继续增量显示，工具组呈紧凑动态摘要 | Stop与插入/排队行为由能力快照决定；composer保留草稿 | 用“发送”一个模糊动作同时代表steer和followUp |
| 等待审批 | 待审批条目在当前工具过程位置可见；概要/参数和影响范围可展开 | 允许/拒绝/作用范围；页头可显示一个需要操作的标记 | 把所有内容藏进侧栏或只提示黄色点 |
| 工具失败 | 当前工具组就地展开错误摘要，正文仍可阅读 | 查看日志/复制原因；是否重试按实际能力 | 正常摘要与失败摘要同样低对比且默认隐藏 |
| 投递未知或断线 | 明确标记属于哪个请求/会话，保留文本 | 重新查询或恢复；不自动重发 | 请求超时直接显示失败并偷偷再次提交 |
| 已结束、有成果 | 最终答案保持主要层级；变更文件/成果入口紧随结果 | 查看Diff/打开文件；支持明确的下一步 | 工具日志仍占整个首屏，最终结果找不到 |
| 面板文件失效/有外部修改 | 当前文件标签与原来源仍可辨认 | 重新定位或处理冲突；保留dirty内容 | 静默打开另一个同名文件、覆盖外改 |

错误采用页内局部反馈；只有阻止整页继续的故障才使用整页错误状态。菜单/flyout关闭后焦点回到触发器，切换会话后的迟到数据先校验身份，不让颜色和提示对应错误任务。系统高对比与放大显示需要单独原生验收。


### 13.6 设置页、扩展页与浮层：从页面入口到控件行

**组件结构与可追踪证据。** `settings-page-b0141d46c881.js` 使用共享 SlateLayout，传入sidebar与路由内容；settings可见分组由 use-visible-settings-sections过滤。导航 cp27039 为flex列、min-height0、flex1；搜索label cp35389，无结果 cp36376 用小字号/次要色/少量顶部间距；cp40157 定义个人、集成、开发等slug分组。搜索/折叠/键盘移动是导航层能力。该chunk没有明确声明整列像素宽度，也没有足够证据单独确认整页纵向滚动面，不能按名字臆测。

`general-settings-cbce7da152c5.js` cp47795 采用panel padding作为组间gap；一类selector cp58984 为w-44（11rem名义宽），cp63194 为h-9/min-w-[12rem]；主题菜单cp60207为max-h-80/overflow-y-auto。这些是特定控件样例，不是所有设置行统一固定高。菜单长度超出时内滚动，说明文本较长时布局必须允许增长。

`settings-unsaved-changes-dialog-4ece8ff184b0.js` cp约580为离页丢弃确认，提供继续编辑与丢弃两条路径。这是dirty-page生命周期，不能当作所有设置都即时保存、每次成功都toast的证据。

**页面设计。** 设置导航解决寻找功能，内容区域先组标题再控件行。大段解释属于helper/detail，不与当前值争夺同等层级；一个清晰组容器足够，行之间用留白或细分隔，不给每个控件再包两层卡片。布局尽量采用“左侧名称/说明，右侧控件”；窄窗时控件转至说明下面并铺满可用宽度，操作列可换行，不能只缩小字号。

**扩展/插件列表。** `plugins-settings-cbe96e4a6efb.js` cp57908 外层限制到viewport/zoom减2rem并overflow hidden；cp71076内容flex-column/min-height0，cp55018主内容flex1/overflow hidden。添加插件modal `add-plugins-modal-7a4f2246fc9b.js` cp18159 的内列表单独overflow-y-auto；cp约5542空态为role=status并有纵向padding。插件页cp31509/35440有max-sm下全宽、换行、列布局、stretch。这类父容器约束、子容器滚动与窄窗重排在Avalonia同样必要。

发行包插件模块包含多类管理tab与搜索。这里研究的是列表/空态/浮层结构，未核实各tab是否当前Codex路由可达；Fufucode不据此新增Apps/Marketplace/MCP能力。真实扩展空态应分开“尚未添加”“搜索无匹配”“加载失败”：前者给添加入口，第二个可清除筛选，第三个给原因/重试，不能一律空列表。

**权限浮层。** `permissions-mode-dropdown-d57f33b74389.js` cp15430 有composer navigation target与loading/label/icon不同trigger形态；cp22617 的标题与说明link同排，选项cp附近为flex行/细分隔，标题14px半粗、副文12px、长文允许换行。模式按runtime/config/managed条件禁用；某些选择进入含当前模型信息的确认dialog。该chunk未声明固定popover宽度，尺寸由共享primitive处理。部分文案指向ChatGPT，共用模块不能视为Codex当前可见菜单的完整证明。

原生应用应借鉴“简短trigger → 可比较选项 → 高影响变更再解释/确认”的层次；Pi审批桥的范围与能力仍按§9.1定义，不能复制Codex权限模式含义。权限trigger需要显示当前值和可用性，loading与disabled可解释；tooltip负责补充，不承载唯一关键原因。

**Fufucode具体落点。** SettingsPage.axaml:10–11已有880居中内容、Margin24/16/24/32和gap14；:34–56为设置卡片，多个模型/代理字段目前IsEnabled=False，视觉重排不能伪造已接线。SettingsPageViewModel.cs:59–84的主题选择立即Store.Update，失败显示error并恢复选中状态；应保留这条现有真实反馈。全局外观设置可即时应用，需重启的会话设置显示待应用/重启，不将“保存到偏好”写成“当前会话已生效”。

ExtensionsPage.axaml:23–25也是880居中列；:74–83的大插图属于Mock空态，真实列表空态目前较轻；:96–112已具有待应用变化、应用/撤销提示，:131–189为扩展项，:196–221为命令列表。建议先补真实空态和条目状态：名称/来源/一行说明左侧，启停/加载状态右侧，错误详情就地展开。继续保留Pi扩展与命令两个语义分组；列表变长后引入筛选/内滚动，设置规模真正扩大再加二级设置导航，不为模仿截图制造空分类。

**验收。** 长中文label/路径和控件不互相挤压；缩窄窗口操作区换行；长菜单/扩展列表只滚对应区域；失败后旧值/待应用状态真实可见；离页dirty处理准确；空态三种原因可分辨；dialog/flyout焦点可返回触发器。未执行本轮UI验收。


### 13.7 聊天页与工作面板：阅读、执行过程、输入各有边界

**实际页面组件关系。** `local-conversation-page-f17300e0b23a.js` cp34955 按状态选择 MainContentLayout 的thread-edge-scroll/full-bleed形式，cp34300传showComposer。`thread-scroll-layout-a4672394afb6.js` cp18500 的转录滚动层关闭浏览器自动anchor并设置底部scroll-padding，内部thread-content独立；cp19770/19826把底部固定内容放到mt-auto/sticky bottom-0，并配置渐隐spacer。它是“正文滚动层 + 底部输入slot”的布局契约，不是给页面贴一个fixed输入框就结束。

原生保留 Fufucode 当前 ChatPage:129–154 的Timeline/Composer分行结构和阅读宽度约束；右面板加入后各自拥有滚动。不要让整个Window包一个ScrollViewer而导致composer被滚走，也不要让输入框覆盖最后一条消息。展开卡片、打开面板和改变输入高度后更新底部可视区/阅读锚点。

**用户消息与助手正文。** `user-message-a41baaa983f5.js` cp12936为全宽flex列的弱背景圆角form；cp24016/26345工具条平时opacity0，在group-hover或group-focus-within显示，触屏hover:none常显（cp25800）。不是给每条正文和动作都画同样高对比卡片。

`local-conversation-turn-202a34d13919.js` cp18700区分user、assistant、activity；cp24226对assistant块组用content-visibility:auto与intrinsic估计240px；cp21389只在completed消息传assistantCopyText。240是浏览器延迟渲染的估计尺寸，不是消息显示高或Avalonia必须采用的item高度。

Fufucode建议用户请求保留弱背景，助手内容按文档段落为主，头像/角色名不必每个细分块重复。消息级更多动作/复制在hover和focus下出现；提供稳定焦点与可选中文本。完整答案的copy与流式状态区分，代码块局部copy可独立实现。列表虚拟化沿§7.1的原生实际测量验证，不照抄CSS content-visibility。

**工具过程。** `active-tool-activity-label-d7b103efce2b.js` 将read/search/edit-files/run-command解释为活动摘要；`mcp-tool-item-content-d44f43616d16.js` cp4300为满宽flex列，cp9827区分active/completed/failed；local turn把工具activity当独立transcript块并可并到相邻assistant轮。通过语义摘要、状态与展开层次把运行过程整理成可读信息，不能只显示完整JSON参数。

Fufucode已在ChatPage:203–295有代码/思考/工具卡片及MaxHeight展开机制。D可以重排为一行活动摘要 + 可展开参数/输出，采用较紧的过程组间距；失败与待审批主动露出摘要。长输出内部有上限与展开，但滚轮边界需要明确：滚到内层边缘时可继续滚主时间线，避免所有文本块都变独立滚动窗。手动阅读长输出时仍按锚点维持位置。

**审批视觉边界。** local turn cp64900分别聚合approvalItem/permissionRequestItems，cp69600有等待答复态；自动审批review细节 `automatic-approval-review-details-e9b98ac35ee9.js` cp2020/2493有展开行与最多80ch说明。通用本地shell/file审批卡片的真实JSX/style本轮未定位；Computer Use专用授权卡不能作为其依据。因而§13.5的审批布局是原创原生建议，未写成Codex本地审批卡的视觉复刻。

**Composer层级。** 代码将composer作为独立slot，shared CSS默认min-height为11个spacing单位、line-height为5个单位，input/footer各有padding；app-primary CSS 的ComposerFooterLabel区分inline布局，DropdownLabel将类别/当前值分层，窄显示值可ellipsis，expanded状态取消限宽；附加rail有above/below位置和tuck/overlap变量。正文输入、紧凑footer和可选附加条分别拥有结构，不能将所有配置按钮插进输入文字的第一行。

Fufucode现有ChatPage:348–480已经具备composer、queue/restore、TextBox和主动作。建议维持上沿待处理条、中部多行输入、下沿配置/命令+右侧主动作；队列无内容时不占位，多项时折叠为简短计数。footer文案过长时截断显示值但popover提供完整当前值；按钮标签/禁用原因不能只剩图标。保留IME组合与已有真实steer/followUp语义。

**Diff面板。** `code-diff-e4a1b1663a6c.js` cp33200文件头为group/diff-header与flex，路径truncate；cp约35000动作hover淡入且focus-visible可见；cp约37000连接编辑器入口；cp约5200选择split/unified及wrap/scroll。`thread-side-panel-tab-content-e8d69c4fcd75.js` 下传stickyHeader/viewType/file-hunk actions。建议右侧先放范围/文件导航，再放粘顶文件头和差异正文；窄面板切unified，宽面板允许split，长代码提供换行/横滚选择。路径截断需能查看完整路径。查看Diff与有写入作用的Git动作明确分组，不让hover中的小图标产生不明覆盖。

**编辑器面板。** `text-file-editor-tab-content.electron-bfdfb0e80411.js` cp99051编辑区relative/min-height0/flex1/overflow hidden，cp98849右上工具按钮，cp95492的frontmatter独立max-height/内滚动；约cp115925有file-tree切换aria入口。原生结构为面板tab与文件路径/dirty标记、可收起文件树、填满剩余高度的文本编辑器；文档属性不是挤占主文本区的长表单。未保存/外改反馈置于文档上下文，不能只在聊天里输出一行警告。

**终端面板。** `terminal-tab-2c37640cd66b.js` cp5008顶部tab行h-6/shrink0，cp15692主体flex-column/min-height0；`terminal-panel-295366ae7230.js` cp14907让xterm占flex1/overflow hidden，提示用absolute overlay。滚动条hover增强属于包内CSS观察，未测触屏效果。原生可采用短tab条 + 占满高度的PTY控件，不把终端正文放入工具卡TextBlock。终端、编辑器和Diff分别维护自己的选择、滚动与生命周期，切tab不重置主对话阅读位置。

这些工作面板依赖§10的功能立项；本轮页面研究没有绕过首版暂不做完整终端/IDE的范围。先改善当前聊天与设置层级，面板使用真实已实现内容，缺失能力不给假按钮。


### 13.8 侧栏、会话行与首页入口：高密度但状态明确

侧栏子研究定位 `app-initial-fd3c4b862660.js`，主研究进一步复核其Guo/Mdo/Ofo/yss片段。文件SHA-256 `8a1dc07f215d429b996f5302de308625fea57adecf9f1fbaf28936d6814b1eea`。yss（cp8490110）按sectionKeys构造置顶、最近、项目及其他条件分组；它是Codex/Work共用侧栏，不能把所有分组都当成当前Codex首屏可见。

Guo（cp6814954）是行基础，默认单行高度取height-token-row、多行取min-height；cp6815910含focus-visible outline、菜单展开背景与selected/highlight背景分支，cp6817229设置aria-current/aria-disabled。Mdo（cp6830022）组装图标、标题、辅行、chips、状态pill、meta与动作；cp6840806响应sidebar-selected/file-drop-active，cp6841621让标题min-width0并truncate。导航行把布局宽度约束、选择语义、拖放/禁用和焦点当成统一结构。

Mdo cp6832751附近会在hover/focus时隐藏某些静态meta，另有reserveHoverActionSpace/useStableTrailingRail参数。它不是简单给行尾永远放三颗按钮；可用固定尾部区域在metadata与动作间切换，保持标题宽度/行高稳定。动作支持键盘焦点与菜单状态，不能只在鼠标经过时可操作。selected语义独立存在，即使某些基础填色复用hover色，也不能在原生实现中把焦点、悬停和当前会话混成一个bool。

Ofo（cp6865100）统一items/getKey/renderRow、expanded/分页、emptyState、isLoading/isLoadingMore与不同placeholder；cp6873478根容器role=list。折叠、加载更多与加载中的占位由列表层控制，条目本身无需复制分页逻辑。自定义分组有明确空态（cp8502417），而不是留一块空白；分组拖放属于共用宿主能力，本轮未完整核实当前Codex交互路径。

**Fufucode原生侧栏方案。** MainWindow.axaml:29固定260列，:35–53品牌/项目区域，:55–64新会话，底部应用导航独立；保留“顶部快捷入口—可滚动会话区—底部全局设置”的骨架。减少品牌区对导航高度的占用；项目名截断但提供完整路径，当前项目与当前会话两个层级不同。会话行建议Grid分“状态图标/标题及可选辅行/稳定尾部区域”，标题min-width0、ellipsis；尾部默认时间或状态，hover/focus时显示更多入口，不挤动标题。

按本产品实际能力选项目/最近/置顶，不复制共用宿主全部分组；尚未实现置顶或搜索时不给可交互假入口。搜索未来可在顶部统一入口，结果应有项目/会话归属；本轮未完整复原全局搜索结果页的真实DOM，相关布局仅作建议。会话较多时才加筛选/分页/虚拟化；保留稳定ID，当前选中项在折叠/恢复后仍可识别。列表错误和空列表显示不同反馈。

**首页与新标签页的证据边界。** `thread-app-shell-chrome-9b8789cd1a61.js` cp34147使用ResizeObserver测最近条目高度与可视区；cp34890起构造newTab.recentWork区，滚动按钮按边界禁用并按完整行滚动。对应CSS `thread-app-shell-chrome-13866043eb5d.css` recent list有独立纵向滚动、隐藏原生滚动条、底部渐隐，在到达底部取消mask；用户交互后可按行snap。行hover/focus-within显背景，某些tile dismiss按钮在hover或focus-visible出现，hover:none常显。这是工作区新标签页的最近工作列表，不是完整证明“新建聊天首页”或所有空态采用同一排版。

新建聊天hero/建议卡片的完整调用布局本轮未复原；不把泛用home CSS或ChatGPT首页当Codex首页截图。Fufucode的原创空会话方案：项目上下文与任务输入构成主区，只有少量真实可执行的建议；建议填草稿的行为明确，输入后自然进入正文+底部composer状态，焦点/草稿保留。现有ChatPage:136的320×200空态插画可作为轻量辅助；不再靠插画尺寸定义页面功能层级。真实加载/缺配置/失败不复用空会话插画。

**验收。** 260–320 DIP侧栏中长项目名/中文标题/时间/状态不溢出；hover/focus动作不改变行高或挤动标题；选中与键盘focus可分；折叠组、加载更多、空列表、失败各有状态；窄窗收起侧栏后仍能返回会话；新会话与恢复会话的焦点/草稿正确。没有实际屏幕走查，以上为设计验收要求。

### 13.9 D轨可执行的页面设计切片

| 顺序 | 本轮页面工作 | 可继续使用的代码 | 必须提交给用户审阅的界面状态 |
|---|---|---|---|
| 1 | 整理页头信息层级和正文/工具视觉权重 | ChatPage现有Timeline、Composer、状态VM、Theme资源 | 空会话、运行、待审批、失败、恢复；页头窄窗与长标题 |
| 2 | 统一间距/字号/圆角与边框密度 | FufucodeTheme/FluentTokens已有语义资源 | 浅色/深色、中文长正文/代码、150%或200%缩放；主次层级可辨 |
| 3 | 侧栏稳定行、尾部动作与窄窗收起 | MainWindow与Sidebar VM、稳定会话ID | 长项目/标题、loading、空列表、错误、hover/focus/selected |
| 4 | Composer上沿队列、输入、footer主动作重排 | InputViewModel及真实投递/恢复链 | 中文IME、长草稿、多个待处理项、停止、未知投递、不可发送原因 |
| 5 | 设置控件行与扩展真实空态/待应用反馈 | SettingsPage/ExtensionsPage现有真实反馈 | 控件未接线、加载失败、保存失败、待重启、搜索无匹配、窄窗 |
| 后续 | 独立工作面板与分割/抽屉 | 依§10的Git/文件/PTY功能契约 | 面板开关不拉走主阅读、dirty/外改/失效、各面板滚动与焦点 |

前五片主要整理现有页面与交互，后续面板依真实功能立项。本轮没有生成产品页面或高保真效果图；文本线框仅作为结构设计，不等于视觉验收。实际实现需D提供可运行界面或截图，再由用户评审密度、中文排版和材料效果；H本次交付为具体设计研究与验收规格。


## 14. 设置目录截图：值得学习的功能与优先级

日期：2026-10-01。依据用户提供的Codex Desktop设置目录截图及本报告已有研究，按Fufucode的coding harness定位筛选。截图证明这些设置入口在用户界面中可见；本节“可以怎么用”是Fufucode设计建议，不仅凭菜单名称推断Codex内部实现。功能细节、移植边界和已有源码锚点参见§7–13。

### 14.1 优先学习

| 功能 | Fufucode可以怎么用 | 对应研究 |
|---|---|---|
| 配置 | 模型、思考等级、代理、工具权限；明确区分已保存偏好与当前会话已生效 | §9.1、§9.4、§13.6 |
| 代码审查 | 变更文件列表、Diff、行级反馈、审查结果回送给Agent | §10.1、§13.7 |
| Git | 查看分支与工作区状态、暂存、提交；保护用户原有改动 | §10.1–10.2 |
| 插件 | 扩展发现、启停、配置、加载状态与失败诊断，接入Pi扩展体系 | §9.4、§13.6 |
| 键盘快捷键 | 命令面板、快捷键自定义、冲突提示、按焦点决定操作范围 | §7.3 |
| 通知 | 完成、失败、等待审批通知；点击返回所属会话，聚焦时可抑制提醒 | §9.3 |
| 使用情况和计费 | 每轮/会话token、成本、当前上下文占用；未知值如实显示，账号额度单独处理 | §8.2、§9.4 |
| 外观 | 明暗主题、正文与代码字号、布局密度、面板尺寸记忆 | §13.1–13.2、§13.6 |
| 个性化 | 全局偏好、项目指令、默认工作方式，作用范围清晰 | 本节建议；复用现有Pi配置/指令机制，具体契约需另核实 |
| 已归档的聊天 | 归档、搜索、恢复，让侧栏保持简洁；归档与删除分开 | 本节建议；桌面元数据分层参见§7.4、§13.8 |

### 14.2 后续独立切片

| 功能 | 价值与实施方向 | 边界 |
|---|---|---|
| Worktrees | 每个任务隔离工作目录，支持并行开发、Diff与合并 | 进入多任务阶段再立项；本报告尚未完整复原Codex worktree创建/挂载链路 |
| 钩子 | 在明确生命周期节点接入格式化、检查、通知 | 优先复用Pi扩展事件；名称相似不表示事件契约相同 |
| 浏览器 | 前端预览、页面验证、截图和控制台检查，形成开发闭环 | 定义预览与自动化控制边界；当前截图未证明其详细能力 |
| 电脑操控 | 让Agent操作原生软件，并呈现操作过程和授权范围 | 独立执行服务与操作权限设计，不能由菜单入口推断已支持的工具 |
| 语音 | 听写任务、口头补充要求，先做可靠输入，再考虑实时对话 | 实时语音是另一项工程，不与听写混为一谈 |
| 导入 | 导入配置、项目或兼容会话，降低迁移成本 | 明确支持格式和导入对象；不承诺任意厂商会话互通 |
| 连接 | 可研究远程主机/执行环境管理 | 截图里的具体含义尚需打开页面确认；此处是候选方向 |

### 14.3 暂不优先与待核实项

| 功能 | 当前判断与理由 |
|---|---|
| 家长控制、受信任联系人 | 对当前本地coding主流程收益较低，不优先跟进 |
| 个人资料、账户 | 本产品优先处理模型供应商凭据与连接状态；完整社交资料/账号体系另需产品需求 |
| Mini与虚拟宠物 | 可作为后期体验增量，当前优先完成真实任务与成果流程 |
| Codex云端、Codex云端（旧版）、Cloud computer | 依赖额外服务与执行宿主，当前不复制Codex专有云端体系；远程执行未来独立立项 |
| 应用快照 | 先核实对象与语义。若涉及工作区检查点/恢复，很值得研究；若主要是应用状态采集，则是不同能力，不能仅凭名称归类 |

截图中的“常规”更适合作为设置组织入口，不需要单独扩成一种新能力；按本产品实际设置内容组织即可。

建议最先深入的六项是**配置、代码审查、Git、插件、快捷键、通知**；进入多任务阶段后增加**Worktrees**。其余优先项可与现有M6设置体验、用量展示和会话管理增量结合。该排序是H轨研究建议，不替代协调者排期，也不代表新增产品能力已实现或验收。

## 15. Wave5 前端设计准则与UI取证补充（2026-10-01）

独立交付见 [frontend-design-guideline.md](../design/frontend-design-guideline.md)：布局/信息架构、15种组件及G资产映射、UniGetUI语义颜色与原生样式、发送/插入/排队/停止/恢复/审批/变更/用量/通知规格、5项ZCode借鉴、Wave5切片与证据附录。用户已授权该新路径；本轮仍不写产品代码、不提交/合并。

本轮三子研究分别负责Codex包/图集、ZCode安装资源/官网、UniGetUI/G映射，主研究交叉核对A8契约和证据口径。双轴决定以main `docs/decisions.md:144–149` 为准；旧D任务书只有Wave1/2，当前Wave5来自F13/queue与用户续任务，不改变轨道所有权。

### 15.1 旧§5与§13未核实项的更新

| 项目 | 本轮新增证据 | 结案边界 |
|---|---|---|
| Desktop当前版本 | 当前AppX **26.928.3736.0**；ASAR根package **openai-codex-electron@26.928.31416**，540844574 bytes | 版本元数据已核实；§6的26.928.2636.0与§13静态值保留为历史快照，不冒充当前路径/当前屏幕computed值 |
| 布局/侧栏/变更卡/composer | 实际查看协调者图集 `00-full.png`，1568×984，有图标轨/项目会话列表/文件摘要/输入footer | 可见形态已补证；点击后行为未实测；图集01/02/03内容与标题不完全对应，不能按README命名当测量证据 |
| 全局命令入口 | 当前包有Cmd/Ctrl+K、Cmd/Ctrl+Shift+P，Files为Cmd/Ctrl+P，Review changes为Ctrl+Shift+G的注册 | 当前静态入口已核实；未在本机触发所有动作，不承诺每账号/焦点域均可用 |
| 变更摘要与撤销 | 截图可见文件清单/展开入口与两个动作；包内sharedSnapshot卡有前3项/展开/inline diff分支 | sharedSnapshot不是live卡点击实测；单文件还原、外改冲突和撤销完整体验仍未核实 |
| 通知种类 | 当前包有turn completion/permission/question设置；官方通知页有前台/后台策略 | 静态/官方文档声称，未验证OS许可、投递、声音播放与点击回会话 |
| 通用审批卡/权限语义 | 既有权限dropdown静态结构与官方安全说明，新增ZCodependingApproval参考 | 本地Codex通用审批卡真实视觉仍未核实；Fufucode布局为原创建议，桥审批不等于OS沙箱 |
| 新聊天hero/搜索结果页面 | 当前包入口及旧最近工作列表证据 | 完整布局/焦点/过滤结果仍未实测；不把共享Work/ChatGPT组件当当前Codex页面 |
| Pi fork与通知前置 | 本轮读取A8 `docs/design/cli-design.md`；它记录固定Pi fork/clone真实无模型核验与TurnSettled设计 | A轨记录非H本轮运行；产品fork元数据/锁交接及OS投递未交付，不能写为UI已验收 |

### 15.2 参考与产品边界

ZCode本机exe **3.14.4.7912**、包 **3.14.4**，只读ASAR。借鉴工具卡状态、任务关联审批、列表组织、独立终端和安全撤销范围；服务名存在与官网呈现描述分开，未运行ZCode。详见准则§6/§8.2，含哈希、偏移与官网链接。

UniGetUI固定快照的颜色/radius/焦点/组件padding/页面250ms转场已给直接锚点。参考没有统一全局字体与spacing网格；本文14/12/20/28字号来自D现状，布局4刻度与768DIP阅读列是本产品建议。G最新版1018生产导出条目已核对，旧13SVG/19PNG的记录不适用于当前素材清单；已接空态与待接error/actions/caption明确分开。

准则中的Diff、撤销、系统通知与fork不是仅画入口就能完成的功能。A8已有两种变更范围和用量unknown契约，未提供patch/±行数/还原/OS投递/产品fork；D先消费真实只读能力，再列新增接口。H本轮只验证文档格式与证据引用，不构建、不运行产品UI或真实Pi模型。

当前包精确文件/符号见准则§8.3；其中`notifications-settings-2e52e83c8237.js`还明确共享声音偏好，因此§5“专门声音设置未核实”本轮补为**设置定义已核实、实际声音/通知触发未实测**。`static-thread-transcript-c7b09aa52eae.js`的前三项/inline diff属于sharedSnapshot，不代替live卡点击处理器；该边界已写入准则I15。
