# E1 Git 工作树与本地审查模块交付记录

任务书：`docs/tasks/hourly-E1-git-review.md`（L3，推荐主模型 `group/auto-qfmodel`）。
日期：2026-10-12（用户时区 America/Tijuana）。状态：**模块交付待接线**。

## 1. 开工身份核对

| 项 | 实测值 |
|---|---|
| 实际 cwd | `C:\Users\mio\.codex\worktrees\aa34\Fufucode` |
| 分支/HEAD | detached HEAD（`git worktree list` 中本工作树独立存在） |
| 起点 SHA | `8f8b3fb2b358df6688b73630f616228599a2a837`（任务书提交，产品基线 `7df4c71`） |
| 继承状态 | 开工时 `git status --porcelain` 为空：无继承的用户未提交内容，不存在覆盖风险 |
| 主区 | `C:/Projects/Fucode/Fufucode`（`main`），本会话未读写 |
| 已读文档 | AGENTS.md、`docs/AI_DEVELOPMENT_PLAN.md`、`docs/tasks/execution-contract.md`、`docs/tasks/session-handoff-protocol.md`、`docs/tasks/development-topology-v2.md`、`docs/tasks/complete-feature-modules.md` C3 段、`docs/records/D4-git-snapshot.md`、`docs/records/harness-backend-2026-10-11.md`、`docs/runtime-baseline.md` |
| 已读真实代码 | `GitWorktreeService.cs`、`GitDiffSnapshot.cs`、`DiffQuery.cs`、`ChangeRangeQuery.cs`、`Automation/Harnesses/*`、`App/Features/Library/*`（生命周期与无头测试范式参照） |

两轮 `interrupted` 后 M 复核为无交付卡、无新增提交、树干净；本轮按原任务书继续，先完成阶段1（真实 Git 列表/诊断 + 范围快照基础 + 临时 Git 测试）并提交，再推进页面、执行接线接口与完整验证。

## 2. 实测前提（本机事实，非假设）

- `git version 2.55.0.windows.5`、`.NET SDK 10.0.401`。
- `git status --porcelain -z` 对中文+空格路径**原样输出、不做八进制转义**；同 `-z` 下 `--name-status` 是 status 与 path 两个独立 NUL 字段，`--numstat` 是 `added\tremoved\tpath` 单字段。这些形状在实现前用临时仓库实测确认，路径处理据此设计。
- `git worktree list --porcelain -z` 支持且保留空格/Unicode 路径；根提交的 `rev-list --parents -n 1` 只输出自身（无第二字段），据此区分“根提交（空树枚举）”与“有第一父”。
- 本机 harness runtime：Pi `@earendil-works/pi-coding-agent` 1.1.0（`C:/Program Files/nodejs/node.exe` + 全局 `dist/bundle/cli.js`）、Codex CLI 0.162.1、OMP 18.9.0、DSH 0.2.0-rc.2；`~/.pi/agent/auth.json` 已配置 `devin` provider。与 `docs/records/harness-backend-2026-10-11.md` 的实测证据一致。

## 3. 实现内容与范围合规

全部落在任务书允许路径内：

- `src/Fufucode.Core/GitWorktreeService.cs`（允许的唯一必要扩展）
  - `ListAsync`：真实仓库工作树只读列表；`StatusAsync`：单工作树 HEAD/分支/detached/变更路径。
  - 诊断枚举 `GitWorktreeInspectReason`：`NotGitWorktree` / `PathMissing` / `GitUnavailable` / `CommandFailed`；非 Git 目录、缺路径、无 git 都有可操作原因，不返回空成功。
  - 未新增 remove/force/clean/reset/push 任何入口（测试用反射断言这条边界）。
  - `CreateAsync` 保持既有实现：从已提交 HEAD 创建、目标已存在拒绝、不复制脏改动、不切换当前会话。
- `src/Fufucode.Core/Features/LocalReview/**`（新增）
  - `LocalReviewScope.cs`：工作区/暂存/单提交/`base..target` 四种范围解析；提交合法性走 `rev-parse --verify --quiet <rev>^{commit}` 且要求 40/64 位十六进制；前导 `-`、含空白、含 `..` 的输入直接拒绝（参数数组，不拼 shell）；合并提交拒绝冒充第一父差异并提示改用显式范围；根提交按 `diff-tree --root` 枚举；工作区范围补齐未跟踪文件（`ls-files -z --others --exclude-standard`），项目外路径排除。
  - `LocalReviewFinding.cs`：严格解析器。逐条校验优先级枚举、路径必须在固定快照内、条目无错误且非 stale、行号存在、证据确实落在所引行；失败分别记为 `path-outside-snapshot` / `line-beyond-content` / `evidence-not-at-line` / `invalid-priority` / `binary-entry-not-locatable` / `unparseable-response` 等 reason。整体不可解析与“无发现”分开，绝不互相冒充。
  - `LocalReviewRun.cs`：`ILocalReviewExecutor` 窄接口、`LocalReviewRunResult`（含 `ReviewComplete` 与 `IsStale` 语义）、有界载荷 `LocalReviewPayload`（真实行号、快照 HEAD、每文件 64 KiB / 总 512 KiB 上限、二进制不注入文本、省略项显式列出）。
  - `LocalReviewHarnessExecutor.cs`：把窄接口适配到已验证的 harness 后端（`HarnessExecutor` + 注册 `HarnessRuntime` + U1 `AutomationStore`）。审查线程的 worktree 是模块自有临时目录，被审查仓库永远不作为 harness 写入目录；终态/Unknown/取消语义沿用后端，不自动重发；过期在快照阶段与复核阶段两处都会标记。
  - `LocalReviewExport.cs`：Markdown/JSON 导出（schema `fufucode.local-review.v1`），必带范围、基准/目标、快照 HEAD、过期状态与被拒绝项。
  - `LocalReviewHistory.cs`：模块目录内一条一文件的 JSON 历史；读回限定在模块目录（越界名返回 null）；数据根由宿主注入。
  - `ReviewGit.cs`：模块共用只读 git runner（`--no-optional-locks`、`-c core.fsmonitor=false`、`-c core.quotepath=false`、超时 20/30s、`Win32Exception` 映射为 `git-unavailable`）。
- `src/Fufucode.App/Features/LocalReview/**`（新增，自包含页面）
  - `LocalReviewViewModel.cs` / `LocalReviewModels.cs` / `LocalReviewPageView.axaml(.cs)`：范围选择、执行/取消、过滤、过期标记、跳转请求、复制/Markdown/JSON 导出、历史与错误展示；`ILocalReviewHostRequests` 为宿主请求接口（打开工作树、编辑器定位、导出路径选择、剪贴板）。
  - 生命周期照资料库页既有约定：临时移出视觉树不取消运行、不销毁视图模型；只有宿主 `Detach` 才停止本模块运行；重复 `Attach` 幂等；切换项目自增操作序号并清空上一项目结果，被替换的旧运行不会回收新操作的忙态；释放后所有命令空转（不投递、不改状态、不写历史）。
  - 窄停靠栏（320 宽）布局：输入/按钮行用 `WrapPanel`，结果区上下堆叠，静态说明在窄模式隐藏、状态行单行截断（沿用 P1 已登记同类缺陷 D-P1-1 的处置方式）。
- `tests/Fufucode.LocalReview.Tests/**`（新增独立测试工程，未接入 `Fufucode.sln`）
  - `Program.cs`：81 项断言，真实临时 Git 仓库 + 子进程夹具。
  - `UiTests.cs`：77 项断言，真实无头 Avalonia 窗口 + 真实编译绑定（`--ui` 分支）。
  - `RealProbe.cs`：可选真实模型最小探针（`--real-probe`）。

**未改动**：`Automation/**`、`GitDiffSnapshot.cs`、`DiffQuery.cs`、`MainWindow*`、`Settings*`、`AppDataLayout.cs`、`Program.cs`、组合根、`Styles`、`Fufucode.sln`、旧 `scripts`、`docs/progress.md`、`docs/decisions.md`、全局 `failure-registry.md`、C3 公共写入范围。`reference/` 未触碰。

### 范围偏差（如实记录）

实现过程中我曾另建 `tests/Fufucode.LocalReview.App.Tests/`（不在允许路径内）。发现后已把其内容合并进允许的 `tests/Fufucode.LocalReview.Tests/`（`UiTests.cs` + `--ui` 分支 + csproj 传递引用剥离），并在提交 `a2b50bb` 中删除该工程的源文件与 csproj。残留的 `tests/Fufucode.LocalReview.App.Tests/bin|obj` 属 gitignore 构建输出、不进仓库，本会话未做递归强删，留待 M 清理（见 §7）。

## 4. 实际验证（已运行）

命令均在 `C:\Users\mio\.codex\worktrees\aa34\Fufucode` 执行。

| 命令 | 结果 | 证据 |
|---|---|---|
| `dotnet build Fufucode.sln -c Release` | 0 警告 0 错误 | `tmp/final-*`、`tmp/e1-*.log` |
| `dotnet build src/Fufucode.Core -c Release` | 0 警告 0 错误 | 同上 |
| `dotnet build src/Fufucode.App -c Release` | 0 警告 0 错误 | 同上 |
| `dotnet build tests/Fufucode.LocalReview.Tests -c Release` | 0 警告 0 错误 | 同上 |
| `dotnet run --project tests/Fufucode.LocalReview.Tests -c Release` | **ALL PASS (81)**，exit 0 | `tmp/e1-git-tests.log`；夹具根 `%TEMP%\fufucode-localreview-91503c65d6674d4a96dfd80e29ff1978` |
| `dotnet run --project tests/Fufucode.LocalReview.Tests -c Release -- --ui` | **UI ALL PASS (77)**，exit 0 | `tmp/final-Fufucode.LocalReview.Tests---ui.log`；夹具根 `%TEMP%\fufucode-localreview-ui-5532733f41674874bb53649458835bb6` |
| `dotnet run --project tests/Fufucode.GitSnapshot.Tests -c Release` | ALL 31 CHECKS PASSED（既有快照契约未被破坏） | `tmp/final-Fufucode.GitSnapshot.Tests-.log` |
| `dotnet run --project tests/Fufucode.Harness.Tests -c Release` | PASS 51 checks，exit 0（复用后端未回归） | `tmp/final-Fufucode.Harness.Tests-.log`；夹具 `%TEMP%\fufucode-harness-tests-4a31195bf2a14a0cba2f4a41916a6bd0` |
| `dotnet run --project tests/Fufucode.Automation.Tests -c Release` | ALL PASS，exit 0 | `tmp/final-Fufucode.Automation.Tests-.log` |
| `dotnet run --project src/Fufucode.App -c Release -- --selftest` | `== 自测结束：0 项失败 ==`，exit 0（约 110 s） | `tmp/e1-selftest.log`；截图 `%TEMP%\fufucode-selftest-1e1d573ef6d5455fa6ebba9e00c59dcf\evidence` |
| `dotnet run --project tests/Fufucode.LocalReview.Tests -c Release -- --real-probe`（显式 `devin/swe-2`，免费档，独立跑两次） | exit 0：`status=Completed complete=True findings=1 rejected=0 stale=False`；`Parser.cs:5` + 证据与快照行匹配；范围一致性 True；HEAD 未变、用户改动仍在；耗时 8.5 s / 17.4 s | `tmp/e1-real-probe.log`、`tmp/e1-real-probe2.log`；证据 `%TEMP%\fufucode-e1-realprobe-1b84fbdbe3ba400582341adbb6c989ab`、`%TEMP%\fufucode-e1-realprobe-22aceae9eeea400582341adbb6c989ab` |

真实探针只回答“传输/终态核对/严格解析/只读边界是否真的成立”，不宣称审查质量；单次有界调用、显式免费模型、临时夹具仓库，未登录、未采购、未派子代理。

覆盖要点（按任务书验收点，非穷举）：

- 工作树：真实列表/状态/分支与 detached、非 Git、缺路径、无 git 诊断；显式创建从已提交 HEAD、新树不含脏改动、原会话与脏改动保持、目标已存在拒绝。
- 范围：四类范围解析、坏 ref 与选项形状拒绝、合并提交不冒充第一父、根提交按空树、`base..target` 记录精确提交、修改/新增/删除/二进制/中文+空格路径按类型如实报告、未变更文件不虚报入范围、项目外路径排除。
- 快照与过期：字节与 HEAD 固定、捕获后源变更 → `IsFreshAsync` false、审查期间并发变化必被标过期（`StaleAfterReview` 与 `IsStale` 一致）。
- 严格解析：快照外路径、超行数、证据不符行、非法优先级、非 JSON、空响应分别拒绝；完成/无发现/响应无效/取消四类终态互不冒充。
- 只读与不越权：审查运行后文件哈希不变、`git status` 仍列用户改动、HEAD 未变（不自动提交/还原）；反射断言无 remove/force/clean/reset/push 入口；打开动作只发宿主请求。
- 页面：真实编译绑定渲染、列表控件与视图模型计数一致、忙态/取消可见、UI 线程不被 IO 阻塞、过滤、跳转带绝对路径与快照行、复制/导出经宿主、历史读写、加载历史不再投递、切换项目清空上下文并取消在途运行、临时移出视觉树不误取消、Detach 停止运行且释放后命令零副作用、320 宽窄停靠栏按钮完整落在可视区。

实现期由测试暴露并修掉的真缺陷（未放宽断言）：`SafeId` 吃掉扩展名导致历史文件枚举不到；取消落在快照捕获阶段会裸抛异常而非给出分类结果；`IsStale` 与 `StaleAfterReview` 状态不一致；`Run` 内联绑定不渲染（改单属性绑定）；窄宽度详情列被压零宽；创建后关键提示被列表刷新覆盖；await 中被置空的 CTS 引用与 Dispose 竞态；项目切换后忙态可能卡死（改为操作序号统一回收）。

## 5. 未运行验证与原因

- 共享外壳接线：`WorkbenchPageRegistry.RegisterOptional` / `MainWindow` / 组合根 / `AppOptions` 属 M 单写，本会话按禁改边界未接，因此**产品入口整项不可用**，不能报整项验收。
- 原生指针、键盘/输入法、焦点、系统 DPI、真实编辑器跳转与真实剪贴板：需在 M 接线后按 V 项以 computer-use 补验；本轮只有无头 Avalonia 证据（不等同原生验收）。
- 暂存/提交/范围三类范围的“精确冻结”：共享快照 API 缺 patch 模式，本模块显式拒绝降级审查，未实现也未验证其审查结果（见 §6）。
- 生产工作树移除/reset/clean/自动提交/合并/push：按任务书不实现，因此无验收；prunable 项只报状态，不提供强制清理。
- 真实 harness 运行中 Detach 的进程树终止行为：模块侧令牌传播与状态分类已测（假执行入口 + 真实子进程 hang 夹具）；真实 Pi 进程被终止后的对账属 harness 后端与 M 接线范围，本轮未跑。
- `Fufucode.Core.Tests` 在本工作树无法完整跑通：该测试依赖被 gitignore 的 `runtime/node/node.exe` 与 `runtime/pi/node_modules`（本工作树未安装），失败点在 `ExtensionUiTests` 的真实 Pi 分支，与本模块无关；同工程内不依赖 runtime 的 294 项断言通过。干净 runtime 环境下的完整回归由 M 处理。
- 干净安装、打包、跨机运行、真实用户仓库：未做、未验；所有 Git 动作只发生在临时夹具仓库。

## 6. 协调卡（共享 API 缺口）

```text
类型：依赖（共享 Core 契约能力不足）
本任务与工作树：E1 本地 Git 工作树与审查模块；C:\Users\mio\.codex\worktrees\aa34\Fufucode（起点 8f8b3fb，交付 HEAD 见 §8）
触发事实与证据：
  已集成契约 src/Fufucode.Core/GitDiffSnapshot.cs 的 CaptureAsync(projectPath, relativePaths) 只做
  「工作区字节副本 + HEAD 相对 diff」，没有 index / commit tree / base..target 的补丁模式。
  实测（tests/Fufucode.LocalReview.Tests，tmp/e1-git-tests.log）：
    - 「暂存范围拒绝直接冻结（缺口显式化）」PASS：CaptureFixedSnapshotAsync(stagedScope) 返回 null；
    - 范围解析本身可用：「暂存范围解析出已暂存文件」「基准..目标范围解析并记录精确提交」均 PASS。
  因此非工作区范围目前只能枚举文件，不能固定「实际被审查的字节与行」；强行复用现有快照会把
  工作区内容冒充暂存/历史内容，产生错误的精确行定位。
缺少什么：GitDiffSnapshot 的一个范围变体（建议签名 CaptureRangeAsync(projectPath, relativePaths,
  GitDiffSnapshotMode mode)，mode = Worktree | StagedIndex | TargetTree(base..target 或 commit^..commit)）；
  StagedIndex/TargetTree 下字节取自 index 或指定 tree、diff 相对该基准，并沿用现有
  stale/HEAD/reparse/8 MiB/取消与 --no-optional-locks 纪律。
已完成和仍可继续的独立部分：
  工作区范围端到端（解析→冻结→执行→严格解析→过期→导出→历史→页面）已实现并有行为证据；
  非工作区范围已完成「解析 + 合法性 + 明确拒绝冻结」，接口位已留好
  （CanFreezeWithSharedSnapshot / MissingSharedApiFor / LocalReviewSnapshotCapability.PendingSharedSnapshotApi）。
建议处理：由 M 在共享 Core 扩展 GitDiffSnapshot（不要求本模块改它）；扩展落地后本模块只需把
  CaptureFixedSnapshotAsync 的范围分支接上新 API，页面与执行链无需改动。
需要 M 作出的具体决定：是否新增上述 mode 变体；是否允许提交范围沿用本模块的
  「合并提交拒绝第一父默认、要求显式范围」规则。
不能继续的部分与原因：M 扩展落地前，暂存/提交/范围三类不能产出精确定位的审查结果；本模块选择
  显式拒绝而非静默降级，因此这三类在接线后仍应标注「未验」。
```

## 7. 待 M 处理的清理项

- `tests/Fufucode.LocalReview.App.Tests/bin|obj`（我误建工程的 gitignore 构建输出）需移除；本会话按“不直接递归删除目录”的约束未动手。文件不在仓库内，不影响收编内容。
- 临时夹具目录（`%TEMP%\fufucode-localreview-*`、`fufucode-e1-realprobe-*`、`fufucode-selftest-*`）作为证据保留；Windows 上部分夹具含只读属性文件导致自动清理抛 `UnauthorizedAccessException`，日志已如实记录，不影响结果判定。

## 8. 交付卡

| 项 | 内容 |
|---|---|
| 任务ID与软件 | E1（Git 工作树与本地审查模块）/ Codex 执行会话，模型 `group/auto-qfmodel`，难度 L3 |
| 工作树绝对路径、分支/HEAD、起点 SHA | `C:\Users\mio\.codex\worktrees\aa34\Fufucode`；detached HEAD；起点 `8f8b3fb2b358df6688b73630f616228599a2a837` |
| 本任务提交 SHA | `ef1576d79a8089f0c56bb79649b0050392a79600` 工作树只读列表/状态；`f6b86cec56f2b9170c4881f5806c7de68676bcdf` Core 范围/解析/适配/导出/历史；`873f733d6964bc4dd1b654474b38dd10b4983c26` 模块定向测试；`57184bd5e0ac2bba424e36de1f6a5ffb87618a4c` App 自包含页面 + 二进制夹具；`f21f31660aa1fa3376b695d3885bb574e5892924` 窄停靠栏堆叠布局；`a2b50bb302561b87cbe0ff7f5417cc4b21d8e62e` 无头页面绑定/生命周期测试 + 窄布局与过期状态修复 + 撤销越界测试工程；其后为本文档记录提交 |
| 改动文件 | §3 清单：`src/Fufucode.Core/GitWorktreeService.cs` + `src/Fufucode.Core/Features/LocalReview/**` + `src/Fufucode.App/Features/LocalReview/**` + `tests/Fufucode.LocalReview.Tests/**` + 本记录 |
| 继承用户改动保护 | 开工时工作树干净，未继承任何未提交内容；全程未对任何用户仓库 stage/commit/reset/clean，未动 `reference/`；只为本任务自身提交与本任务临时夹具提交 |
| 实际验证命令、结果、证据位置 | §4 表：sln/Core/App/模块测试均 0 警告 0 错误；模块 81 项 + 无头页面 77 项全通过；既有 GitSnapshot 31 项、Harness 51 项、Automation 全通过；应用 `--selftest` 0 项失败；真实最小探针（显式 `devin/swe-2`）两次 exit 0，终态 Completed、定位与范围一致、只读成立。日志在本工作树 `tmp/`，夹具与截图在 `%TEMP%` 对应目录 |
| 未运行验证与原因 | §5：共享外壳与原生交互（禁改边界，归 M/V）、暂存与提交/范围精确冻结（共享快照 API 缺口，§6）、危险 Git 操作（按任务书不实现）、真实 harness 进程终止对账（属后端与接线）、`Fufucode.Core.Tests` 真实 Pi 分支（本工作树缺被 gitignore 的 runtime）、干净安装与真实用户仓库（不在授权） |
| 公共接线需求、接口/入口/依赖 | 1) `WorkbenchPageRegistry.RegisterOptional("local-review", "本地审查", <icon>, "local-review", create, release)`，create 内 `new LocalReviewPageView()` + `Attach(new LocalReviewViewModel(appDataLayout.Root, executor, host))`，release 调 `Detach()`；2) 数据根经 `AppDataLayout.Root` 注入（模块只写其下 `local-review/{scratch,evidence,history}`，不碰 desktop.db）；3) `ILocalReviewExecutor` 用 `new LocalReviewHarnessExecutor(() => new AutomationStore(projectDb), registeredRuntime, dataRoot, projectId, model, provider)`，runtime 显式模型；4) `ILocalReviewHostRequests` 由宿主实现 OpenWorktree（shell/编辑器）、JumpToLocation（编辑器定位）、PickExportPathAsync（StorageProvider）、CopyToClipboardAsync（TopLevel.Clipboard）；5) 快捷键沿用公共动作 ID，由 M 注册；6) Core `GitDiffSnapshot` 范围变体（§6）。模块未挂外壳前不得报整项可用 |
| 状态 | **模块交付待接线**（工作区范围端到端已实现并有本地行为证据；非工作区范围因共享快照 API 缺口显式拒绝，属部分能力；页面未接共享入口，原生交互未验） |
| 原会话是否仍在写代码 | 否。本会话已提交实现与记录，停止写入 |

## 9. 停止写入声明

本会话完成实现、验证、记录与交付卡后**停止写入**：不再修改代码、不再提交、不动工作树状态（不清理 §7 残留目录）、不 push、不改写历史、不删工作树、不登录/采购/公开部署、不自行开始下一任务包。暂存/提交/范围的精确冻结等待 M 落地共享快照 API；原生验收等 M 接线后补。

## 10. 记录提交与最终 HEAD 复验（追加）

- 本节 §1–§9 的实现与记录提交：`a131aa219d90e286366076a7c21217b3999056e1`（docs(E1): record …）。
- 在该 HEAD 上复跑收尾验证（`tmp/deliver-main.log`、`tmp/deliver-ui.log`）：
  - `dotnet build Fufucode.sln -c Release` → 0 警告 0 错误；
  - `dotnet build tests/Fufucode.LocalReview.Tests -c Release` → 0 警告 0 错误；
  - `dotnet run --project tests/Fufucode.LocalReview.Tests -c Release` → `ALL PASS (81)`，exit 0，夹具根 `%TEMP%\fufucode-localreview-cdd1b4873f73405cbc0a8356d0e45db3`；
  - `dotnet run … -- --ui` → `UI ALL PASS (77)`，exit 0，夹具根 `%TEMP%\fufucode-localreview-ui-0d31d519cd1946e8ab60c723a0a244e3`。
- 本追加说明作为最后一条文档提交；它的 SHA 需由 `git log -1` 读取（记录文件不能包含自身提交哈希，不虚构）。此提交后本会话停止写入。
- 收编范围核对：`git diff --name-status 8f8b3fb..HEAD` 只含 §3 列出的允许路径文件（1 条 `M` 为 `GitWorktreeService.cs`，其余为新增），无禁改文件、无 `Fufucode.sln` 变更、无 `tmp/` 内容入库。

## 11. M 审核返工（2026-10-12）

审核来源：主区 `docs/records/M-E1-review-2026-10-12.md`，决定 `rework / not_integrated`，被审核 HEAD `2b695cc2173c64b0a862ddb0748e7b156edd28e1`。旧交付的 81 项与 UI 77 项通过没有覆盖七类真实缺陷；旧完成说明不代表审核接受。

本轮实现提交：`d54c87e354905cb9dbc181193d8108064892acc7`。仍在原 aa34 工作树，未同步 main，未改共享快照、Automation、MainWindow、sln 或权威进度。

修复内容：

1. 超过 2 MiB 的 UTF-8 响应直接分类为 `oversize-response`，`Truncated=true`；不按 UTF-16 字符位置切片，750000 个中文字不会抛异常。
2. 优先级只接受四个命名枚举值，拒绝数字字符串（含 `99`）；空白证据拒绝为 `missing-evidence`，非空证据仍核对所引快照行。
3. 载荷总 512 KiB 预算包含提示、元数据、行号、文件内容和遗漏说明，全部按 UTF-8 字节计算。超大元数据也守预算；`BuildResult` 明确返回未送审路径。
4. 在空发现分类之前复核快照；审查中源变化加 `findings:[]` 返回 `StaleAfterReview`，不会提前返回 `NoFindings`。
5. 响应列出发现但全部无效，返回 `InvalidResponse`，`ReviewComplete=false`；与真正空发现严格区分。
6. 每文件、总量、二进制或缺字节导致内容未送审时，返回 `IncompleteScope`，`ReviewComplete=false`。结果、JSON、Markdown 和持久历史包含未送审清单；不报告全范围干净。页面沿用 `StatusText` 展示不完整状态。
7. 移除无条件 finally 删除。Unknown、Interrupted、派发后取消保留 scratch，结果暴露 `RetainedScratch`，运行身份和后端证据保留用于对账。异常不能证明 writer 停止时同样保留。明确终态才释放 scratch；释放不等于自动解除后端 Unknown。

另外修复取消发生在后端 claim 写入之前时 `HarnessRunStore.Get` 抛 `KeyNotFoundException` 的竞态。缺少 claim 时不伪造后台运行记录，取消仍可展示；保守保留已经创建的 scratch。

### 反例红绿证据

主区 `tmp/e1-audit/Program.cs` 的反例忠实移植到允许路径 `tests/Fufucode.LocalReview.Tests/Counterexamples.cs`，新入口 `--ce`。为复现原缺陷，暂存本轮 Core 修复后，在 `2b695cc` 的原 Core 上运行相同反例，再恢复修复；没有 reset、改写历史或修改主区。补充字段采用反射读取，让原始反例能在 pre-fix 上编译。临时 stash 已恢复，不留本轮 stash。

- 红：`tmp/e1-ce-red.log`，exit 1，10 个失败断言覆盖七类问题（CE2 分两项，scratch 分多个断言）；多字节载荷实际 601493 字节，源变动空响应报 NoFindings，全部无效报 Completed。证据目录 `%TEMP%/fufucode-e1-counterexamples-ff2d2a47c1f341549194dc138ebdf540`。
- 绿：`tmp/rework-ce.log`，exit 0，20 项 PASS，`CE ALL PASS`。包含 750000 中文响应、20×10000 中文载荷、源变化空响应、全部无效、每文件/总预算省略、超大元数据、真实子进程启动标记后取消、缺少终态的子进程退出、明确终态释放。启动标记证明 CE7 取消发生于实际派发后，而非快照阶段。证据根由该日志第一行记录。
- 原模块回归：`tmp/rework-module.log`，exit 0，`ALL PASS (84)`。保留原行为覆盖，二进制未送审断言改为直接检查省略提示、字节未注入、未送审清单与字节预算，未放宽原验收语义。
- UI 回归：`tmp/rework-ui.log`，exit 0，`UI ALL PASS (77)`，真实编译绑定、停靠移动、取消和释放测试通过。
- 相关回归：`tmp/rework-snapshot.log` 31 项通过；`tmp/rework-harness.log` 51 项通过；均 exit 0。
- 构建：`tmp/rework-build.log`（模块工程，包含 Core/App 引用）与 `tmp/rework-sln.log`（solution）均 0 警告、0 错误；`git diff --check` 通过。

本轮没有运行模型探针，全部使用无模型的临时 Git 与真实子进程夹具。以前的真实模型证据仅是旧交付记录，不冒称为本轮新验证。

### 返工交付卡

| 项 | 内容 |
|---|---|
| 任务与会话 | E1 原任务返工，L3，Codex；不另开任务、不派子代理 |
| 工作树 / 起点 | `C:/Users/mio/.codex/worktrees/aa34/Fufucode`，detached HEAD；原起点 `8f8b3fb`，本轮基准 `2b695cc` |
| 修复提交 | `d54c87e354905cb9dbc181193d8108064892acc7`；其后仅本节记录提交，最终 SHA 见交付回复或 `git log -1` |
| 改动范围 | Core/Features/LocalReview 下 Export、Finding、HarnessExecutor、Run；允许测试目录下 Program 与新增 Counterexamples；本记录。禁止路径未改，继承修复保留，不操作用户数据 |
| 验证 | 反例红 10 FAIL → 绿 20 PASS；模块 84、UI 77、快照 31、Harness 51；相关构建 0 警告、0 错误。日志见上表述 |
| 接线要求 | M 继续共享索引/历史快照与公共 App 接线。新 `IncompleteScope` 与 `UnreviewedPaths` 需展示；`RetainedScratch` 由人工/宿主对账后处理，不自动删除或自动解除 Unknown |
| 未验 | 本轮未重新运行真实模型；原生 IME/焦点/DPI、公共入口、共享暂存/历史冻结仍由 M 接线后验；沿用 §5 环境限制 |
| 状态 | 提交待复核，模块交付待接线；本轮不宣称已接受或已集成 |
| 停写 | 记录提交后停止写入；不 push、不改写历史、不清理目录、不开始下一包 |
