# D4 非依赖只读快照契约交付记录（D4-SNAPSHOT 子任务）

日期：2026-10-10。范围：仅实现不依赖 G1 的 Core 只读固定差异快照契约。**整项 D4（暂存/提交/工作树管理）未完成，G1 仍未交付/未验证。**

## 允许范围与已完成

- `src/Fufucode.Core/GitDiffSnapshot.cs`（新增）：`GitDiffSnapshot.CaptureAsync` / `FileDiffSnapshot` 记录 / `CheckFreshAsync` 新鲜度复核。
- `tests/Fufucode.GitSnapshot.Tests/`（新增 net10.0 控制台测试，仅引用 `Fufucode.Core`，无额外包）。
- 本记录文件。

未修改：solution、Core csproj、共享测试 Program.cs、App/UI、暂存/提交/删除任何产品功能。

## 契约事实（供 O4 消费）

- 显式指定相对路径列表；去重、统一 `/` 分隔；`..` 越界条目返回 `path-escapes-project`。
- 记录实际 `rev-parse --show-toplevel` 仓库根与 `rev-parse --verify HEAD` 提交。
- 每条目捕获：是否存在、是否被 Git 跟踪（`ls-files --error-unmatch`）、`FileDiff`（复用 `DiffQuery.FileAsync`，不复制解析器）、源文件字节与 SHA-256。
- 快照是读取时刻的字节副本，**不是**原子 Git 快照。捕获前后各读一次文件状态与 HEAD；任一变化把该条目标记 `Stale`，整体 `Error` 置 `concurrent-changes-detected`。`IsFreshAsync`/`CheckFreshAsync` 供事后复核。
- 拒绝边界：reparse 点（junction/symlink）沿路径逐级检测拒绝，不跟随到仓库外；非 Git 目录返回 `NotGitWorktree`；空路径列表返回 `NoPathsCaptured`；单文件 >8 MiB 标记 `file-too-large`；取消令牌直达 `OperationCanceledException`，不吞掉、不伪造成功。
- 只读保证：所有 git 调用带 `--no-optional-locks`、`-c core.fsmonitor=false`；`--no-ext-diff`、`--no-textconv` 由 DiffQuery 统一保证；不 stage/commit/restore。

## 实际验证（已运行）

`dotnet run --project tests/Fufucode.GitSnapshot.Tests/Fufucode.GitSnapshot.Tests.csproj -c Release`：21 项断言全部通过，exit 0。覆盖：

- 真实临时 Git 仓库捕获，实际 HEAD 与 toplevel 比对一致。
- 已跟踪修改、未跟踪新文件、中文+空格路径、越界拒绝条目。
- 捕获后修改源文件：旧快照字节不变，`IsFreshAsync` 返回 false。
- 捕获不写索引（`git diff --cached` 为空）、不改源文件内容，`git diff HEAD` 只见手工改动的两个文件。
- reparse 点（`mklink /J`）拒绝、取消传递、空请求显式状态、非 Git 目录拒绝。
- `dotnet build src/Fufucode.Core/Fufucode.Core.csproj -c Release`：0 警告 0 错误。

## 未验证 / 不在本子任务范围

- G1 仍 open；其验收（及依赖 G1 的 D4 其余部分：暂存/提交/工作树列表/脏保护）未做、未验。
- O4 实际消费此契约未验（O4 依赖 G1+D4 整项）。
- App/UI 集成、computer-use 差异选择验证未做（本子任务为纯 Core 模块）。
- 并发写入竞态只靠捕获前后对比检测，非锁保证；极端同时写入可能漏检——消费方须以 `Stale`/`IsFreshAsync` 为准。

## 交付卡要点

模块就绪待 M 集成接线；不集成 main、不 push、不清理工作树。

## Rework 修复（第二轮，review.decision=rework）

审核发现三处缺陷，已在原允许文件范围内修复并新增回归测试（总数 21 → 31）：

1. **子目录项目状态错位**：`CaptureStateAsync` 原来按仓库根解析相对路径，`DiffQuery`/字节读取按 `projectPath` 解析；子目录项目会把根的同名文件状态拼到子目录文件上。现统一按 `projectPath` 解析（仍为同一文件身份），新增"根 a.txt 已跟踪、sub/a.txt 未跟踪且内容不同"反例验证 TrackedAtCapture=false、字节为 sub 内容。
2. **删除的已跟踪文件被误标未跟踪**：`TrackedAtCapture` 原只在文件存在时查 `ls-files`。现索引身份与存在性独立查询：删除后 `ExistedAtCapture=false` 而 `TrackedAtCapture=true`，且删除前的快照不再 fresh、删除后新快照 fresh。
3. **带错误/已过期的条目仍可报 fresh**：`CheckFreshAsync` 现在遇 `entry.Stale` 或 `entry.Error`（含 `path-escapes-project`、`file-too-large`）直接返回 false；新增含错误条目的快照与无错误的干净快照对照测试。

复跑：`dotnet run --project tests/Fufucode.GitSnapshot.Tests -c Release` 31/31 通过；`dotnet build src/Fufucode.Core -c Release` 0 警告 0 错误。G1 与整项 D4 仍未验。

## M 主区收编交付卡

- 任务：D4-SNAPSHOT；运行：791deb5bb655448bad77f3995c5b6191。
- 审核：本运行 accept/pending_wiring，绑定来源 HEAD `ef18d70263fb0076719222d5fdafe507523a37d0`；来源进程停止，工作树干净，原始起点之后仅四个允许文件。
- 主区起点：`6d0ccb27e7e4345462296d9c1608d0053ca11320`。
- 真实 no-ff 合并：`d50ef96b3689595ea13713938a8decde95c0a411`，两个父提交分别为主区起点与来源 HEAD。未改写来源历史。
- 实现与接线：仅收编独立 Core 公共契约及独立行为测试。SDK 默认编译包含 GitDiffSnapshot.cs；独立测试通过 ProjectReference 消费 Core。不需要改 Core 项目、solution、共享测试入口或 UI。
- 验证：在主区运行批准脚本 `pwsh -NoProfile -File tmp/agent-flow/verify-D4-SNAPSHOT.ps1 -Repository C:/Projects/Fucode/Fufucode`，exit 0；31 项检查通过；Core Release 构建 0 警告、0 错误。未修改验收脚本或放宽断言。无模型测试未启动模型。
- 证据：`tmp/agent-gate/791deb5bb655448bad77f3995c5b6191.integration-verify.log`；继承内容保护清单 `tmp/agent-gate/791deb5bb655448bad77f3995c5b6191.integration-preserve.json`。
- 边界复核：路径状态与字节使用同一 ProjectPath；跟踪身份独立于磁盘存在性；错误或 Stale 条目拒绝 fresh；路径越界/reparse 拒绝、取消传播经过实际测试。并发变化采用前后哈希/HEAD 比对及读取后复核，标注检测到的变化；不是原子快照，不承诺检测所有瞬时变化。极端并发与运行中取消竞态未新增行为验证。
- 未验证：G1/G2、整项 D4、D5、O4 消费端、UI/原生交互；本次不解锁这些门。
- 交付状态：本独立模块已收编 main；不代表产品整项验收。下一步由外部 M 更新权威记录；本轮不派工、不 push、不清理工作树。
