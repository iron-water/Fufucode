# Fufucode Agent 协作约定

开始工作前阅读 `docs/AI_DEVELOPMENT_PLAN.md`（权威开发计划）和本文件。旧轨道任务书与队列保留为历史记录，不再自动派发或续接。

下一阶段执行会话与主 agent 必须读取 `docs/tasks/execution-contract.md`、`docs/tasks/session-handoff-protocol.md` 和 `docs/tasks/launch-prompts.md` 中当前任务段落。`docs/面向人类/` 仅作用户概念与操作说明，不作为 AI 执行协议的真源。

## 自主推进与角色

- 主控与执行者均须读取需求、现有代码及开发记录，以实际交付为目标，不只给方案。先核实自己的角色、任务、工作树和已有状态；已有任务先对账续接，不重复创建，不将自己自动视为主控。
- 开工先形成简明计划，明确任务、依赖、允许修改范围和验收；普通实现选择按最保守可用方案自主处理，只把必须由用户决定的产品方向、登录授权或付费问题交给用户。局部阻塞不停止其他独立工作。
- 主控负责架构、共享接口、复核、接线与集成；独立开发使用受管执行会话和独立worktree，最多两个执行者并行，同一文件仅一个写入者。执行者自主推进本任务内部步骤，不越范围、不自行集成main或派下一任务。
- 主控每次派工提供完整prompt：任务ID、难度、具体推荐模型与理由、起点、工作树/允许路径、依赖、验收、交付位置、真实主控threadId和回传授权。按难度与成本选实现模型；测试、验证、试跑显式使用最低成本可用模型，首选devin/swe-2，不依赖默认模型，不用昂贵测试lead。纯本地无模型构建不要求模型参数。

## 主动交接与消息授权

本节依据用户2026-10-15在“主agent (提问)”线程（`01a12596-9397-7132-b8c9-095b38fa726f`）直接给出的要求更新。用户授权原文：

> 我明确授权：你向本项目受管执行会话派单、返工和协调；这些执行会话向你所在的主控线程主动回传交付、阻塞和协调消息。派单时附本授权原文和真实主控threadId。

- 授权限于本项目受管会话及绑定主控，不是任意线程或外部收件人。主控派单附上本直接人类授权原文、来源和真实目标ID；执行者不得把普通父线程请求当作额外人类授权，也不得猜测收件线程。
- 成功、失败、阻塞、部分完成都必须由执行者主动交接，不等用户或主控催促。不能在允许范围内解决时立即通知主控：任务ID、失败步骤、实际错误、副作用、仍可继续的工作；随后保存证据并恢复临时状态，无法恢复必须明确报告，不能静默结束或自行越界修复。
- 结束前必须先写入交接卡并读回确认可读。卡包含：任务ID与状态、工作树路径、真实HEAD、提交SHA或未提交及原因、改动、证据、实际验证、未执行/未验证、未完成项、公共接线需求、临时权限/设备/进程状态、恢复结果和`stoppedWriting`。真实停止源码/状态写入并处理自身临时进程后才可写`stoppedWriting=true`，不能预先承诺停写或清理未知进程/设备。
- 既有机器卡使用`writerStopped`时保持其协议字段，不擅自破坏schema；人类交接卡中的`stoppedWriting`与机器卡`writerStopped`须表达同一真实状态。阻塞协调时若仍继续独立工作，明确尚未停写，不伪造true。
- 交接卡落盘且停止写入后，执行者主动向绑定主控发送任务状态、卡路径、真实HEAD、遗留问题和交付版本标识，并核实消息工具返回成功；最终回复仍展示交接结果。仅在执行线程输出最终答复不算完成主控交接。发送失败或结果不明，在本线程最终回复明确“主控回传失败”及卡路径，保留证据，不假称已送达、不盲目重复发送。
- 主控以“可读交接卡、主动回传、真实停写”三项对账，核实执行轮次结束、实际HEAD与卡一致后再独立审核、整合或打回。`idle`、exit0或卡自称成功均不代表完成；消息在执行者最终回复前到达时只登记待核验，不提前整合。
- 主控发现缺卡、缺回传或未停写，立即按缺项追补并告知用户，不留待定时任务才处理；未知状态先对账，不重复派工。按task/run/交付版本/HEAD去重，返工优先原会话、原工作树、原主模型，不以无必要“收到”互发消息造成唤醒循环。
- 每次处理后，主控更新权威进度与必要决策，主动输出下一步操作卡/完整prompt；执行者只更新自己的任务记录并回传。依赖通过且有执行空位才派新任务。

## 定时补漏与能力边界

- 仅指定主控会话使用每两小时一次的定时补漏；已有同用途任务则更新，不重复创建，不向执行会话或提问会话添加定时任务。本项目现有automation为`fufucode`，目标管理线程`01a10ae0-9bb9-78f3-b2b3-fd3ceeb34d07`；角色/目标有新直接用户决定时再更新，不因另开对话重新创建。
- 定时检查漏报、失败、阻塞和执行空位，继续推进；主动回传与主控当前轮次核查是主流程，定时只是兜底。全部约定目标真实验收通过后，主控停止该定时推进并给最终交付总结，不能因单任务完成提前停掉。
- 缺少创建会话、消息或定时工具时，如实说明具体缺项、交接卡路径和替代处理，采用可用方式继续；未执行写“未执行”，未验证写“未验证”，不得假称自动化已启用或消息已送达。规则文件更新不代表已传入运行中的旧工作树；主控在下一次正常派单/续接明确带入，不假定执行者自动获知。

## 当前后端架构（2026-10-17）

- 默认后端是 **FUMO 原生内核（纯 C# 实现）**，零 Node/Python/外部 CLI 依赖；`EngineId.NativeCSharp` 的持久化标识为 `fumo`。
- Pi/OMP/Codex/DSH/Claude 是**可选后端插件**，默认不随包发布。`src/Fufucode.Plugins.Pi/` 为 Pi 后端插件，`Fufucode.Pi.dll` 只在安装该插件后存在。
- 默认安装包（`package.ps1`/`package-native.ps1`）不得包含 `runtime/node`、`runtime/pi`、`extensions/fufucode-bridge` 或 `Fufucode.Pi.dll`。
- 新代码不要重新把 Pi 类型引入 `Fufucode.Core`/`Fufucode.App`/`Fufucode.Cli`；跨后端能力走 `ISessionCoordinator`、`IRpcClientFactory`、`IBackendSessionFactory`、`BackendId`。
- 五类后端结构同级：`src/Fufucode.Plugins.Pi|Omp|Claude|Codex|Dsh` 各自携带协议适配器；Core 不再引用 `Fufucode.Claude`。插件未安装/未启用时后端必须报告不可用，不能回退到其它引擎。
- 运行时与协议载荷分离，且**所有工具链同级**：node / bun / python / git-bash 与 pi / omp / claude / codex / dsh 共用 `ToolchainCatalog` 一张表、同一探测与安装接口。主包不含任何运行时；用户系统已有则直接使用，缺失时 `fufucode runtime install <id> --from <zip|url> [--sha256 HEX]` 装到 `%LOCALAPPDATA%\Fufucode\runtime\<id>`。执行期解析为用户自带优先、系统兜底，启动时由 `ToolchainEnvironment` 注入 PATH。运行时包统一由 `scripts/packaging/package-toolchain.ps1 -Id <id>` 生成。禁止再为 Pi/Node 写特例路径。
- 插件分发包由 `scripts/packaging/package-native.ps1` 产出到 `out/plugins-dist/`；用户用 `fufucode plugins install <id> --from <zip|url>` 部署到 `distribution/<id>`。
- 历史 Pi 文档与记录保留为档案，不代表当前默认行为；决策以 `docs/decisions.md` 2026-10-17 条目为准。

## 构建部署与更新通道（2026-10-17）

- `build-info.json` 是"部署"与"更新"的分界：每个构建产物写出 `{version, builtAtUtc, channel, commit, githubRepository?}`，构建身份 = `commit-构建时间戳`（`BuildInfo.BuildId`），同提交的重复构建身份也不同——"改完重新构建"天然就是一次部署，不需要版本号参与判断。
- **本地快速构建（部署）**：`BuildDeploymentService` 按构建身份判断，备份将被覆盖的文件→按内容增量复制（大小+写入时间，不做全量 SHA-256）→锁定文件写入 `restart-pending.json` 待重启；`Program.Main` 须在加载程序集之前调用 `ApplyPendingOnStartup` 完成替换。发现顺序：`FUFUCODE_UPDATE_SOURCE_DIR` → `%LOCALAPPDATA%\Fufucode\updates\local` → 应用目录向上找 `out/app`、`out/build/app`、`out/build-native/app`；指针构建经 `SourceDirectory` 解析真实目录。
- **发布更新**：`UpdateService` 只在 `channel=github` 且写入仓库坐标时启用，比较版本号、走 GitHub Release。两条路径互不回退，发布构建不能通过本地部署覆盖。
- 打包脚本写标记：`FUFUCODE_BUILD_CHANNEL`（默认 `local`，发布用 `github`）与 `FUFUCODE_GITHUB_REPOSITORY` 是发布渠道的注入点；`dotnet run` 无标记时 `BuildInfo.ResolveCurrent` 回退到程序集版本并视为本地构建。
- 设置页"构建与更新"是单一入口：按当前构建渠道分派部署或更新，不展示另一条路径的入口。
- 原生包按 RID 构建：`package-native.ps1 -RuntimeIdentifier`（默认 `win-x64`）。Release 自包含发布启用 `PublishTrimmed=partial`，App 与 CLI 共用一份裁剪运行时（`System.Console` 已 root，打包期做 CLI 启动冒烟）。跨平台保持同一源码按 RID 分别出包，不把其它平台载荷放进 Windows 安装。
- 内存优化模式：`settings.json` 的 `performance`（`softwareRendering` / `disableVisualEffects` / `reduceTerminalScrollback`）默认全关；运行时只读 `MemoryProfile`。软件渲染需重启，其余即时生效。新增可能显著占用内存的功能时，优先接入该开关而不是默认开启。
- 默认发自包含包（无需 .NET）；已有 .NET 10 Runtime 的用户可选 `-FrameworkDependent` 瘦身包（`out/build-native-slim`，安装器 `Fufucode-Native-Slim-Setup.exe`）。瘦身包不裁剪，按 RID 手工移除其它平台后端。

## 后台常驻与单实例（2026-10-20）

- 默认仍是关窗即退出。`AppSettings.Background.CloseToTray`（默认 false）开启后，标题栏关闭/Alt+F4 只隐藏主窗口与浮动辅助栏，会话与后端子进程继续运行；托盘左键恢复，菜单含显示主窗口/退出；只有托盘退出或显式退出结束进程。
- 单实例按登录会话生效（`Local\` 命名 Mutex + 激活事件）；第二个进程唤醒已有窗口后自行退出。自测、无头预览、原生模块自测与探针模式不参与单实例。
- 隐藏时不销毁会话、不暂停 Browser Use/CDP 页面；WebView2 降内存依赖既有可见性/遮挡路径（内存优化分支）。托盘创建失败时不隐藏窗口，设置被关闭时若窗口隐藏会先恢复。

## 插件化优先（用户2026-10-16明确要求）

- 当前拆分：可选页面已插件化——`workspace.browser`、`workspace.terminal`、`workspace.library`、`workspace.local-review`、`automation.auto-dev`。它们默认不安装，只在插件市场安装/启用后贡献页面；未安装时侧边栏、停靠区和动作目录都不显示。
- 核心工作台（会话管理、文件、变更、侧边聊天）保留为 App 核心页，**不再称为插件**；它们随包提供，不走插件市场，也不因为“插件化”名义在市场里增加条目。
- “用量”页已移除，相关内容并入性能中心；不再有独立 `usage` 页面、动作或设置分类。
- 新产品能力新增/重构时先判断能否成为真实插件包；可选实现不得默认塞进 App/Core。

- 产品以小核心与真实插件包组织。新增/重构功能无论大小，先判断能否独立安装、更新、禁用与卸载；不要默认继续塞入App/Core或MainWindow partial。源码可留在同仓，但可选实现及专属依赖不得因此随默认包发布。
- 插件目录、JSON状态、隐藏入口和功能开关不等于安装架构。安装必须落实真实载荷获取/校验/部署/注册，卸载必须落实运行停止、注销、载荷移除和明确的数据保留策略；资源仍占用时报告待重启或待清理，不假称已卸载。
- 原生内核、基本聊天/存储、权限审批、插件宿主与恢复基础保留核心。用户明确自动开发也可以作为插件：其页面、规划、调度和专属后台实现可以独立包，不能借“默认工作流”强行留在App/Core。默认安装/启用策略与架构拆包是两回事；未有新决定前保留此前默认工作流，是否默认捆绑列为待记录策略，不阻挡先拆包。四后端维持已定默认启用策略，外部运行环境是否就绪单独显示。
- 浏览器控制、电脑控制等已指定可选能力默认不安装；浏览器页面与AI控制分离。逐项候选及迁移验收见 `docs/research/pluginization-audit-2026-10-16.md`，未拆包不得宣称插件化完成。M派工与执行者交付均核对依赖和默认包实际载荷。

## 文件所有权（硬约束）

| 角色 / 协议 | 职责 | 文件范围 / 门禁 |
|---|---|---|
| 主 agent 全权 | 统一规划、实现、审查、提交与集成，维护交付记录 | 全仓；`reference/` 第三方克隆保持只读；不推送、不改写历史 |
| 特种兵任务包协议 | 按明确任务包临时委派，写明目标、允许路径、验收条件和交付证据；完成即收回 | 仅任务包指定路径；不得扩大范围或自行集成；必要时使用临时 worktree，由主 agent 收编与移除 |
| 验证门前置 | 实施前明确验收条件，提交与集成前运行与风险相称的必要验证 | 代码变更按影响执行构建、定向测试及必要行为验证；纯文档检查差异与格式；未运行项明确标为未验证 |

## Git 与工作区

- 主仓库当前位置：`C:\Projects\Fucode\Fufucode`，分支 `main`；按用户重建目录后的实际路径执行，历史轨道分支保留供追溯。执行会话使用独立工作树，不按历史prompt的旧路径回到主区。
- 每个可验证增量小步提交；提交只包含本次任务内容，不重复 `git init`。
- 临时 worktree 开工前同步 main；主 agent 核对未提交内容和未合并提交后，先收编有价值内容，再明确记录作废项。
- 移除工作区使用 `git worktree remove <path>`，不得直接递归删除 worktree、仓库、会话或记忆目录。
- `docs/decisions.md` 保持追加记录；不把旧队列 pending 状态视作新授权。

## 并行 Feature（2026-10-21）

- **执行模型开工前必须读 `docs/tasks/parallel-feature-brief.md`**；主控派工时把该路径写进 prompt，旧工作树没有该文件时把 brief 的核心命令直接附在 prompt 里。
- **小改动直接在 `main` 上做**（一行样式、文案、单个 bug、少量文件）：提交后运行 `feature-publish.ps1 -Main`，不要新建 feature。
- 只有功能过大、改动面广或与其它会话并发冲突明显时，才用 `fucode/feature-worktree.ps1 -Name <id>` 建独立分支/工作树；feature 一旦可用就**尽早**用 `feature-merge.ps1` 合回 `main`，不长期挂在分支上。
- **已并入 `main` 的 feature 分支视为终结**：不再在其工作树上开发，后续修改回到 `main`。
- 一个工作树只有一个写入者，主工作树同一时间也只有一个写入者。
- 执行模型收尾必须自己运行 `fucode/feature-publish.ps1` 发布，不得让用户代为构建；它检查分支/干净状态、生成 `branch@commit` label、构建到主工作树 `out/review/<分支名>`、核对产物，并**立即启动独立审阅窗口**。同一 feature 重新发布会先停掉上一次由它启动的窗口。用户不需要去审阅板里挑构建。裸 `publish-local.ps1 -ReviewLabel ...` 仅作故障回退，相对 `-OutputRoot` 也以主工作树为基准。不得覆盖 `out/app`、写本地更新指针或停止别人的运行实例。
- 主工作树的小改动用 `feature-publish.ps1 -Main` 构建并启动 `main@commit` 窗口；它和 feature 窗口走同一套隔离输出、label 和数据根。
- 用户点头后，用 `fucode/feature-merge.ps1 -Name <id>` 合入：脚本要求 feature 已同步 main、两边工作树干净，只做 fast-forward；不合条件就拒绝，不在 main 留半合并状态。合入后运行 `feature-publish.ps1 -Main` 看集成结果，再决定是否 `feature-worktree.ps1 -Remove`。
- 合入 main 不会自动更新已有 feature 分支；它们需要在自己的工作树 `git merge main` 后重新发布。新工作树从 main 创建，自动带上。
- 审阅板 `fucode/review-parallel.ps1 -NoBuild -Open` 只用于盘点/回退；常态是模型发布后窗口直接出现。每项带 `branch@commit` label、独立 `--data-root`，窗口标题显示 label，落后于当前 HEAD 会标注。
- 模型最终回复必须回报：分支、HEAD、label、审阅目录、构建退出码、已运行/未运行测试、未提交改动和待接线项。
- 工作树清理只用 `feature-worktree.ps1 -Remove`，且必须干净并已合入 `main`；禁止强删未提交工作。详见 `docs/operations/parallel-review.md` 与 `docs/tasks/parallel-feature-brief.md`。

## 交付与诚信

- M受管无人值守任务采用`docs/tasks/agent-event-protocol.md`结构化事件；只在agent停止后接收交付，审核按绑定runId/taskId/HEAD的明确decision分流，不按正文关键词判断。模块待接线是有效交付，不要求受限worker越权接共享入口。

- 新派任务推荐主模型及备选从当前canonical的`group/`目录选择，完整prompt首行直接写完整模型ID、任务难度与推荐理由，不得仅写档位或简称。打回保留原模型与测试最低成本规则不变。

- M新派任务按 `docs/tasks/session-difficulty.md` 综合评估难度并给具体模型建议，prompt内保留等级。打回默认原会话/原主模型；仅较原始派工升高至少两级时提示可用更强子代理辅助限定修复，不自动换模型或派发。测试/试跑仍最低成本。

- 执行模型按上文“主动交接与消息授权”完成落卡、读回、停写和主动回传；仅最终回复展示卡不算完成交接。部分交付/外部阻塞如实标注；主控处理后主动给下一步操作卡。

- 中文回答，简洁干练；以事实与最终交付为准，不作防错性质的全仓遍历或冗余回归。
- 未实际运行的验证不得标记为通过；未验证项和阻塞点写入交付记录。
- 不虚构 RPC 命令、协议能力或模型行为；以 `docs/runtime-baseline.md` 的实测记录为准。
- 测试不得靠放宽断言制造通过。
- GitHub 查询优先使用已认证的 `gh` CLI，仅在不可用时退回匿名请求。
