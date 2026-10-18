这份报告要给无文件访问能力的 ChatGPT 看，所以必须把所有事实内联进去。我先把各轨道进度、验证记录、决策记录和代码规模数据收集齐。

数据基本齐了，最后补 B、G 两轨的近期记录：

资料收集完毕。以下是完整报告——自包含、可直接整段复制给 ChatGPT（它无需访问任何文件）。报告之后是给你的话术。

---

# Fufucode 开发技术报告（自包含文本版）

**报告日期**：2026-10-01 ｜ **代码基线**：main 分支 @ `7f67054` ｜ **项目开工**：2026-09-28（至今 4 天，229 个提交）
**报告性质**：全文自包含，无外部文件依赖。所有数据均为实际运行/实测记录，未验证项如实标注。

## 1. 项目定位

Fufucode 是一个 **Windows 优先的桌面 AI coding agent 客户端**：C# / .NET 10 / Avalonia 12.1.3 构建 UI 壳，通过官方 **Pi**（`@earendil-works/pi-coding-agent`，Node CLI agent 内核）的 stdin/stdout JSONL **RPC 协议**驱动真实 agent 能力（模型流式对话、文件读写、shell 执行、会话持久化、分支、压缩）。

核心架构约束（开工即定，写入开发计划）：

- **不移植 Pi 内核**、不自实现模型供应商协议、不自实现 Agent 循环——这些全部由官方 Pi 子进程承担；
- 桌面端负责：UI、消息重建与流式渲染、工具执行前审批、会话元数据（SQLite）、进程宿主管理、打包发布；
- 插件机制复用 Pi 官方扩展（TypeScript），不自造 C# 插件 SDK；
- 首版明确不做：完整 IDE、插件市场、自研终端模拟器、多 Agent 编排、远程 RPC、自研沙箱。

**产品对标**（2026-10-01 用户定案）：功能面向 Codex Desktop 看齐，视觉面向 UniGetUI（Fluent v2）看齐；下一阶段前端按“五区布局”重构（左栏任务列表+一般选项 / 中央对话主面板 / 右栏视图窗口栈 / 底栏终端 / 独立设置界面）。

**当前状态一句话**：M0–M5 里程碑全部完成并通过真实链路验收；M6 进行中（Core 完成、UI 大部分完成）；M7 打包发布链路已全绿（安装器升级/回退演练 PASS，代码签名除外）。已有可日常试用的安装包（1.1.0，142 MB），并有同命令面的独立 CLI。

## 2. 总体架构

### 2.1 分层结构

```text
Avalonia Views / ViewModels（Fufucode.App，13891 行 C# + 11 个 AXAML）
        │  只依赖冻结契约库
Fufucode.App.Abstractions（254 行，纯 BCL，冻结的 IChatSession 接口）
        │
Desktop 会话协调 + 本地元数据（Fufucode.Core，3338 行 + SQLite desktop.db）
        │
C# Pi RPC 客户端 + 子进程宿主（Fufucode.Pi，524 行，零第三方包）
        │  stdin/stdout JSONL（UTF-8，LF 分帧，单写入队列，按请求 ID 路由）
官方 Pi 子进程（Node 24.15.0 + pi-coding-agent@0.87.1，版本锁定）
  ├─ 模型流式请求 / Agent 循环 / 工具执行（bash、powershell、read、write、edit、find、grep、ls）
  ├─ 会话 JSONL 持久化 / 分支(fork/clone) / 压缩
  └─ TypeScript 扩展：fufucode-bridge（审批桥，511 行 TS）+ 用户扩展
独立 CLI（Fufucode.Cli，1105 行）：与 GUI 同命令面的无头前端，复用 Core
测试（Core.Tests 2449 行 / FakePi 996 行 / Pi.Tests 84 行）
```

### 2.2 关键机制

- **传输层**：单序列化写入路径防 JSON 交错；按 ID 匹配响应、支持乱序；连接代次（generation）隔离进程重启前后的请求与事件；有界队列，超限显式报错不静默丢弃；stdout 读取与 UI 渲染严格解耦。
- **消息重建**：按 `contentIndex` 维护文本/思考/工具块，delta 只追加对应块，`message_end` 为最终依据；usage 是累计快照（不逐条相加），压缩后未知值保持 null 不显示为 0。
- **审批链路（安全核心）**：Pi 端 `fufucode-bridge` 扩展拦截 `tool_call`（只读工具自动放行；写文件/shell 强制 `ctx.ui.confirm`；未知工具 fail-safe 确认），经 `extension_ui_request/response` RPC 往返到达桌面弹窗。Core 维护“审批桥探针”三态（Unverified/Verified/Unavailable）——**探针非 Verified 时 `CanSend=false`，产品模式拒绝提交任何 prompt**，杜绝无审批执行工具。应答令牌绑定请求 ID + 连接代次 + 参数快照，一次批准只对应一次请求；拒绝/超时经磁盘断言验证无副作用。
- **项目信任**：RPC 模式无法显示 Pi 内置终端信任提示，Core 主动检测受保护资源（项目 `.pi/`、技能、SYSTEM.md 等），无决定时抛 `ProjectTrustDecisionRequiredException` 阻止启动，决定存 desktop.db，启动时显式传 `--approve`/`--no-approve`。
- **会话持久化**：对话权威数据归 Pi 的 JSONL 会话文件；桌面 SQLite 只存项目/会话索引/草稿/偏好，不做第二份历史。会话独占用 `FileShare.None` 锁（GUI 与 CLI 互斥已实测）；`get_entries` + `since` 游标做增量恢复；SQLite schema 迁移前先在线备份。
- **冻结契约**：`IChatSession`（发送/停止/事件流）在独立契约库冻结，App 与 Core 互不引用、只引用契约库，集成零循环依赖；接口变更需双轨道同步。

### 2.3 代码规模（实测，不含 obj/bin）

| 组件                         | C# 行数                     | 说明                      |
| ---------------------------- | --------------------------- | ------------------------- |
| Fufucode.App（Avalonia 壳）  | 13,891                      | 视图/VM/集成装配/自测框架 |
| Fufucode.Core（协调/持久化） | 3,338                       | 含 Microsoft.Data.Sqlite  |
| Fufucode.Cli（无头前端）     | 1,105                       | 全命令面                  |
| Fufucode.Pi（RPC 协议层）    | 524                         | 零第三方包                |
| Abstractions（冻结契约）     | 254                         | 纯 BCL                    |
| Core.Tests                   | 2,449                       | 自研 selftest 风格        |
| FakePi（假 Pi 子进程）       | 996                         | 真实管道测试              |
| Pi.Tests                     | 84                          | 协议帧测试                |
| Pi 扩展（TS）                | 511                         | 审批桥 + demo/fixture     |
| **合计**                     | **约 22,400 行 + 11 AXAML** | 229 commits               |

依赖面刻意极小：Avalonia 12.1.3、CommunityToolkit.Mvvm、Microsoft.Data.Sqlite，其余全为 BCL。

### 2.4 运行时基线（实测记录）

| 项             | 值                                                           |
| -------------- | ------------------------------------------------------------ |
| .NET SDK / TFM | 10.0.401 / net10.0                                           |
| Avalonia       | 12.1.3（Themes.Fluent + Inter 字体 + Headless 自测）         |
| Node（受管）   | 24.15.0，`runtime/node/node.exe`                             |
| Pi             | `@earendil-works/pi-coding-agent@0.87.1`，lockfile 锁定 81 个传递依赖 |
| 包体积         | 原始 ~585 MB（.NET 自包含 209 + Pi 依赖 226 + Node 87 + Pi dist 18），zip 184 MB，Inno 安装器 ~142 MB |
| 启动实测       | Pi spawn 124 ms → Pi 就绪（首次 get_state 成功）720 ms；进程退出 62 ms、零残留 |
| 发布完整性     | SHA256 清单 15,465 文件全量校验                              |

## 3. 开发流程：多 Agent 协作制

本项目由**多个 AI agent 并行开发**（用户明确授权），4 天完成上述全部工作。这是本次评估的重要背景——流程本身是评估对象。

### 3.1 轨道分工与文件所有权（硬约束）

| 轨道 | 职责                                                    | 驱动者                | 独占路径                                                     |
| ---- | ------------------------------------------------------- | --------------------- | ------------------------------------------------------------ |
| A    | Core/RPC/CLI 全部后端                                   | codex CLI（自动派发） | `src/Fufucode.Pi`、`Core`、`Cli`、`tests/`、`scripts/` 主体、`runtime/` |
| B    | 常驻验证执行员（只记录不修复）                          | Maka（GUI 派发）      | `scripts/bridge-verify`、`demo-verify`、`upgrade-watch`      |
| C    | 原负责 App 外壳；M2 集成因 933 分钟无产出被收回，已退休 | ——                    | 无（贡献保留：契约库、外壳、时间线、自测框架）               |
| D    | App 前端全部                                            | devin                 | `src/Fufucode.App/`                                          |
| F    | 打包/安装器/升级回退/诊断                               | codex CLI             | `scripts/packaging/`                                         |
| G    | 品牌与图像资产（原创）                                  | codex CLI             | `assets/`                                                    |
| H    | 对标研究（Codex/ZCode/UniGetUI）                        | codex CLI             | `docs/research/codex-feature-study.md` 等                             |

规则：每轨**只写自己的独占路径**；发现他人文件的问题记入自己的进度文件，不直接改；发现跨轨需求走“交接单”（如 D 的 Wave2 走查产出 F-1～F-4 问题单交 F）。

### 3.2 工作区与集成

- 每轨独立 **git worktree**（如 `C:\Users\21894\.codex\worktrees\track-a-core\Fufucode`），共享同一 `.git`；禁止在他人工作区做任何 git 操作。
- 集成方向：各轨分支 → `main`，**由轨道自己合并自己的分支**（自动派发轨道由驱动器代合并）；合并前该分支构建+测试必须全绿。
- 开工新任务前必须 `git merge main` 同步；找不到任务书 = 没同步。
- 小步提交，每可验证增量一提交。

### 3.3 派发机制

- `docs/archive/coordination/queue.md` 为无人值守工作队列；`scripts/orchestrator.mjs --tick` 自动派发标记 `codex` 的条目（派发→等待→验证 build/selftest/Core.Tests→合并→标记 done，FAIL 弹回标 blocked）。
- 手动轨道（Maka/devin/桌面 codex）走 GUI 投递，状态同样记入队列。
- 当前队列状态：A2–A9、F2、F3、G 返工、H 研究全部 done；D2（Wave5 前端重做）进行中。

### 3.4 诚信规则（全轨强制）

- 未实际运行的验证不得标记为通过；每个进度条目区分“已实测（附命令与证据路径）/仅源码审查/未验证”。
- 不虚构 RPC 命令、协议能力、模型行为——一切以 `docs/runtime-baseline.md` 实测记录为准（例如官方文档漏录 14 个已发射事件，靠实测协议面对比脚本发现）。
- 测试不得靠放宽断言制造通过；失败如实记录（含端点限流等环境性失败与代码缺陷的区分）。
- 文档体系：`docs/AI_DEVELOPMENT_PLAN.md`（权威计划）、`progress-*.md`（每轨）、`decisions.md`（只追加的决策记录，149 行）、`runtime-baseline.md`（协议能力实测表）、`verification.md`（验证证据）、`../archive/coordination/queue.md`（队列）。

## 4. 开发进度

### 4.1 里程碑总表

| 里程碑 | 内容                           | 状态                                                        |
| ------ | ------------------------------ | ----------------------------------------------------------- |
| M0     | 基线与可重复启动               | ✅ 完成                                                      |
| M1     | JSONL 传输 + 进程宿主 + FakePi | ✅ 完成                                                      |
| M2     | 流式聊天 Core + 桌面集成       | ✅ 完成（真实模型多轮对话验收通过）                          |
| M3     | 编码工具 + 执行前审批          | ✅ 完成（真实 Pi 审批允许/拒绝/超时磁盘断言）                |
| M4     | 会话持久化/恢复/侧栏           | ✅ 完成（真实模型写盘→关闭→重开恢复）                        |
| M5     | 插件交互与管理                 | ✅ 完成（四类扩展弹窗 + 启停 + 信任衔接）                    |
| M6     | 设置与桌面体验                 | 🔶 进行中（Core 完成；UI 完成大半；Markdown 行内渲染等遗留） |
| M7     | 打包/升级/发布                 | 🔶 主体完成（安装/升级/回退 drill 全绿；签名、干净机器未验） |

### 4.2 时间线与各轨明细（2026-09-28 → 10-01，4 天）

**A 轨（后端，最重）**：
- M0–M4 Core 一次推进：传输层、协议 DTO（source-generated JSON）、会话协调、FakePi 管道测试、真实 Pi 验收、SQLite 元数据、项目信任（F01）。
- M6 Core：JSON 设置引擎（原子写/迁移/防覆盖）、模型/思考等级 RPC（set_model/set_thinking_level + get_state 核对）、代理与兼容端点注入（密钥只写环境变量引用）、Windows 凭据管理器（CredReadW/WriteW，环境变量回退）、脱敏日志与诊断导出。
- A3/A5：**CLI 全命令面**（chat/sessions/models/settings/extensions/diag/approve，GUI 完整无头等价物）。
- A6：**CLI 真实验收**——真实 Pi + 本地 OpenAI 兼容 SSE 模型跑通 11 类命令边界（审批允许→文件生成；拒绝→exit 3 + 文件不存在；Ctrl+C=130 via 真实控制台事件；GUI-CLI 会话锁 sharing violation）+ 打包接线（CLI 随包 + PATH 注册 + 双启动器，17 项 launcher 验收）。
- A7：**无头 UI 控制面**——CLI `ui snapshot/screenshot/invoke` 直接驱动真实 Avalonia Headless 控件树（AutomationPeer 元数据，11 项断言，拒绝业务操作与反射）。
- A8：Wave5 契约侧——变更范围查询（workspace=Git 工作树 / session=已确认 write/edit 两口径）、用量统计（unknown=null 语义）、离线只读会话报告（32 MiB 上限 + partialTail 标记）；fork/clone/树导航协议逐项实测（确认 `navigate_tree` 不存在）。
- A9：**Pi 追新基建**——探针套件（10 场景 × 双版本各 10 PASS + 自测 14）、升级 SOP（旁装→探针→diff→决策门→升级→回退）、FakePi 契约化（25 个显式 RPC handler + 20 类事件 encoder）；0.99.2 候选版差异报告已产出（新增 mcp/codemode/tool-search 内置工具、`/llama` 命名变化），**升级决策门待人工放行**。

**D 轨（前端）**：
- M2 集成（接管自 C）：组合根、真实流式链路、停止/草稿、历史回放。
- M3/M4：四类审批弹窗（非模态、fail-safe）、侧栏会话管理（新建/切换/重命名/草稿隔离）、真实模型全链路（Atria 免费 OpenAI 兼容端点）。
- M5：扩展管理页（发现/启停/安全重启/失败回滚）、Fluent 视觉基座（UniGetUI 令牌移植、Fluent System Icons、Mica 条件启用）。
- Wave1：设置页（主题/模型/思考/代理/端点/关于，只接已交付后端）、插入(steer)/排队(followUp) 双投递、中文 IME 组合守卫（`TextPresenter.PreeditText` 方案）。
- Wave2：**安装版全流程走查**——发现并修复 D-1（无项目选择）至 D-8 共 8 档问题，产出 F-1～F-4 打包问题单。
- Wave4：**UniGetUI 实机 11 项视觉对齐**（页过渡 250ms/28px/样条曲线、导航选中条双 Bezier、控件几何、双主题）——12 项过、6 项差异如实记录（自绘标题栏等未做）。
- Wave5（进行中）：按 codex-feature-study 路线图 + Codex Desktop 图集重做前端。

**F 轨（发布工程）**：
- 打包 spike：可重复 `package.ps1`（self-contained + 受管 Node/Pi）、干净启动验证（剥离 PATH 后自测全绿）、§12 口径实测数据。
- Inno 安装器 + SHA256 完整性清单 + **升级/回退演练 drill**：装 0.9.0 → 布局+清单校验 → selftest → 覆盖升级 1.1.0 → 完整性 15,465 文件全绿 → 卸载（用户数据保留）→ 回退重装 0.9.0，全链路 PASS。
- F3：D 走查发现的打包四坑全修（包内缺审批桥、启动器丢参、损坏安装器根因=编译被超时截断、快捷方式 WorkingDir）。

**B 轨（常驻验证）**：三轮固定验证组全绿（build/selftest/realpi 自测/Core.Tests/clean-start/bridge-verify 8/8/demo-verify 10/10）；第三轮新增**安装版 CLI 冒烟**（包内审批桥拦截链实测成立）；发现 1 中 2 低问题（exit 3 语义、索引字段与 cwd 不一致），只记录不修。

**G 轨（资产）**：原创资产返工至 UniGetUI 水准——441 SVG / 576 PNG / 1 ICO（七层尺寸）+ 32 张三列对照图，含四态动作图标与标题栏三态按钮；全部原创，manifest 登记。

**H 轨（研究）**：Codex CLI/Desktop 功能研究（22 项采用判断 + P0/P1/P2 排序）；Desktop ASAR 逆向级深挖（滚动锚点、命令面板快捷键、审批作用域、PTY，证据到 byte 偏移 + SHA256）；前端设计准则草稿（组件/令牌/交互规格），正按用户“五区骨架”定案重组。

## 5. 验证体系与实测数据

### 5.1 测试分层（五层金字塔）

1. **协议 fixture**：合成 JSON 验证帧规则/乱序/超限（Pi.Tests）。
2. **FakePi 子进程**：真实 stdin/stdout 管道模拟分段/乱序/退出/审批（Core.Tests 主体）。
3. **真实 Pi + 临时项目**：官方行为、扩展、工具、信任、fork（Core.Tests 真实段 + probe 脚本）。
4. **真实模型端到端**：Atria 免费 OpenAI 兼容端点（`api.atria-asi.ai`，key 走环境变量不入库）多轮对话/工具循环/停止/恢复。
5. **桌面 UI**：Avalonia Headless 自测框架（`--selftest` 152 项 mock 全绿）+ `--selftest --realpi`（真实 Pi + 真实模型 210 项）+ CLI 无头控件树控制（A7）。

### 5.2 当前测试数字（全绿）

| 套件                                      | 结果                                                         |
| ----------------------------------------- | ------------------------------------------------------------ |
| Core.Tests（FakePi + 真实 Pi 段）         | 272 PASS / 0 FAIL                                            |
| Pi.Tests                                  | 18 PASS                                                      |
| App `--selftest`（mock）                  | 152 ok / 0 失败                                              |
| App `--selftest --realpi`（真实 Pi+模型） | 210 ok / 2 项环境性 FAIL（demo 扩展路径与无 key 模型段，非产品缺陷，已如实分档） |
| bridge-verify / demo-verify               | 8/8、10/10                                                   |
| Pi 探针套件                               | 0.87.1 与 0.99.2 各 10 PASS / 0 FAIL + 自测 14 PASS          |
| 安装 drill                                | 装/升/完整性/卸载/回退全 PASS                                |
| 构建                                      | 0 警告 0 错误                                                |

### 5.3 已真实验证的能力清单（摘要）

- 真实模型多轮流式对话、工具调用审批（允许→写盘生效；拒绝/超时→磁盘断言无副作用）、停止（clear_queue+abort）后不自动继续、关闭重开完整恢复、会话独占（GUI↔CLI 互斥）、扩展启停安全重启、项目信任四组合实测、CLI 全命令面、无头 UI 控制、安装升级回退、包内审批链路、中文/空格路径、IME 组合不误发送。

### 5.4 未验证项与已知问题（诚实清单）

**发布缺口**：代码签名（无证书）；干净机器安装验证（drill 在开发机）；MSI/MSIX 形态。
**模型缺口**：付费远端真实模型（现用 Atria 免费端点 + 本地合成 SSE 模型）；真实供应商重试/计费行为。
**桌面体验缺口**：交互式桌面人工验收（真实 IME 输入、Mica 观感、窗口动画、弹窗手点）；Markdown 行内渲染未做（行内代码/粗体显示原始记号——日常观感差距最大项）；自绘标题栏与 icon-only 导航轨未做（Wave4 已知差异）。
**功能缺口**：OS 通知未实现（设计已完成）；fork/clone 的桌面元数据/锁迁移未实现；diff patch 内容契约未实现（A8 只有文件范围）。
**协议风险**：Pi 0.99.2 升级待决策门（探针差异已列：内置工具面变化、命令命名变化）；升级 SOP 与回退边界已备。
**遗留缺陷**：Pi 进程在一次 write 工具后 0xC0000409 退出（一次未复现，交 A 调查）；不支持思考的模型上 set_thinking_level 被 Pi 静默钳制（需 get_available_thinking_levels）；sessions 索引 project 字段与实际 cwd 不一致（低）。
**性能**：§12 口径的进程组内存、长会话、窗口可交互时间未复测（首版数据已有：Pi 就绪 720ms）。

## 6. 关键决策记录（decisions.md 摘要）

1. 桌面用 Avalonia（弃 WinUI3）；不移植 Pi 内核；单会话单进程绑定。
2. Node 24.15.0 + Pi 0.87.1 版本锁定，lockfile 固定；禁止启动时自动 `pi update`；升级走 `runtime/<version>` 并存 + 验证后切换 + 保留旧版。
3. `IChatSession` 冻结契约库（App/Core 零循环依赖）；usage 累计快照语义；停止 = 清队列 + 中止。
4. 审批探针三态门控：桥未验证则禁止发送 prompt（fail-safe 优先）。
5. 密钥只存 Windows 凭据管理器或环境变量引用，任何入库文件不含密钥。
6. 回退边界诚实声明：保留旧程序 ≠ 可安全降级新版本写出的数据。
7. 视觉令牌移植自 UniGetUI（MIT），图标用 Fluent System Icons（MIT），不复制任何参考产品资产。
8. 双轴对标定案（2026-10-01）：功能向 Codex Desktop、审美向 UniGetUI；前端五区布局为下一阶段定案。

## 7. 进行中与下一步

1. **D2 Wave5**：五区布局前端重做（进行中）。
2. **H 轨重组**：既有研究材料按五区重新归类 + 补每区功能清单研究。
3. **四大新功能可行性（已完成技术选型）**：Markdown 渲染与代码 diff 页 = 纯托管，低成本；Web 浏览器 = Avalonia 12 官方开源 WebView（Windows 上即 WebView2）；内置终端 = C# ConPTY + xterm.js（复用 WebView 宿主基建）。
4. **Pi 0.99.2 升级决策门**：按 A9 的 SOP 由协调者放行。
5. M6 收尾（Markdown 行内渲染、标题栏）+ M7 收尾（签名、干净机器验证、§12 复测）。

## 8. 局限与风险（供评估者重点审视）

- **单机协作的验证盲区**：所有验证在同一开发机；无干净环境矩阵、无 CI。
- **免费端点依赖**：真实模型验收依赖 Atria 免费端点（有 429 限流，已按“端点不可用≠缺陷”分档处理）。
- **测试框架自研**：无 xUnit 等成熟框架（selftest 风格），断言质量依赖纪律。
- **无人值守多 Agent 的合并质量**：驱动器自动合并仅验证 build/自测/Core.Tests 三关，UI 行为回归依赖各轨自测与走查。
- **Windows 单平台**：Avalonia 跨平台能力未使用（WSL/远程明确延后）。

---

# 给你的话术

**开场 prompt（贴在报告前面发给 ChatGPT）：**

> 你是一名资深桌面应用架构师与研发效能顾问。下面是一份多 AI Agent 协作开发项目的自包含技术报告——你没有文件访问能力，请完全基于报告文本评估，不要假设报告外的信息，对报告中标注“未验证”的项不要当作已完成。请从四个维度输出评估：① 架构设计（分层、契约冻结、审批安全模型的合理性，有没有过度设计或欠设计）；② 开发流程（多 Agent 轨道制、文件所有权、队列驱动的优劣，这种模式有什么系统性风险）；③ 进度与质量可信度（4 天 229 commits 达到该完成度是否合理，验证体系哪些环节最薄弱、最可能存在“自证清白”盲区）；④ 下一步路线（五区重构与四大新功能的优先级建议）。要求：直接指出问题，不要客气；每个批评点给出可执行的改进动作；最后按“如果只做三件事”给出优先级。

**评估过程中可以追问的问题（挑着用）：**

- “审批桥探针 + CanSend 门控这套安全模型，和业界 agent 产品的权限系统相比处于什么水平？有什么绕过路径？”
- “让 AI agent 自己写验证、自己跑测试、自己声明通过，这份报告里哪些证据设计让这个风险变低了？哪些地方仍然存在自欺空间？”
- “我打算接下来同时做五区 UI 重构、Markdown 渲染、浏览器面板、终端——这个顺序合理吗？会不会摊子铺太大？”
- “22,400 行 C# 自研了 RPC 客户端而不用 MCP/HTTP，这个决策你怎么看？锁定 0.87.1 不升级的策略呢？”
- “如果我只有一个人的精力做后续维护，这个六轨道协作体系应该怎么收敛？”

**两个注意点：**

1. 报告里“已验证/未验证”的区分是这个项目最值钱的部分——明确要求 ChatGPT 把“诚实清单”当作评估输入而不是免责声明，让它判断清单本身有没有漏报。
2. 如果 ChatGPT 给的某些建议涉及具体技术选型（比如终端方案、WebView 方案），注意它可能没有 2026 年的最新信息（Avalonia 12 官方 WebView 开源、探针套件等都是近期变化），可以把我前几轮给你的技术结论作为补充材料贴给它。