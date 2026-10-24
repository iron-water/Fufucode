# 并行 Feature 执行说明（模型必读）

日期：2026-10-21。适用于所有在独立工作树里开发 Fufucode feature 的模型会话。
配套规则见 [AGENTS.md](../../AGENTS.md) 的“并行 Feature”段，命令细节见
[parallel-review.md](../operations/parallel-review.md)。

## 一句话

**默认在 `main` 上改。** 只有功能过大、改动面广或并发冲突明显时才开 feature，
并且越早并入 `main` 越好；已并入 `main` 的 feature 分支随即作废，不再继续使用。

## 先选车道

- **小改动、快速修复（默认）**：直接在主工作树 `main` 改并提交，收尾运行
  `pwsh fucode/feature-publish.ps1 -Main`，构建并弹出 `main@commit` 窗口。
  不要为一行样式、一个文案、一个小 bug 新建 feature。
- **大功能、会阻塞别人、需要长期试错**：才用独立 feature 工作树，收尾运行
  `pwsh fucode/feature-publish.ps1`，构建并弹出该 feature 的窗口。
- **尽早合并**：feature 达到可用状态就尽快 `feature-merge.ps1` 合回 `main`，
  不长期挂在分支上；`main` 前进后 feature 必须 `git merge main` 再发布。
- **不复活旧分支**：已经并入 `main` 的 feature 分支视为终结，后续修改一律回
  `main`（或按新任务另开分支），不得继续在旧工作树里开发。
- 一个 feature = 一个分支 = 一个工作树 = 一个写入者；同一时间主工作树也只有一个写入者。

用户点头后由主控运行 `fucode/feature-merge.ps1 -Name <id>` 合入 main。feature 必须先
`git merge main` 并重新发布，脚本只做 fast-forward，不合条件就拒绝。

## 开工前先核对

```bash
git rev-parse --show-toplevel
git rev-parse --abbrev-ref HEAD
git status --porcelain
```

必须满足：

- 当前目录是你的 feature 工作树，不是主工作树 `C:\Projects\Fucode\Fufucode`。
- 分支是 `feature/<id>` 或任务指定的分支。
- 工作树里没有另一个模型正在写的迹象；同一工作树只有一个写入者。
- 已有未提交改动时先判断归属：是本任务的就继续，不是本任务的就停下报告，不要覆盖。

如果你在主工作树，不要直接开始并行 feature。让主控先创建：

```bash
pwsh fucode/feature-worktree.ps1 -Name <id>
```

如果你已经在独立工作树，不要再创建第二个工作树。

## 开发规则

- 只改任务允许的路径；共享入口、`main` 集成、最终发布由主控负责。
- 不合并 `main`，不推送，不改写历史，不删除别人的工作树。
- 每个可验证增量小步提交；提交信息说明本 feature 的改动。
- 不把“模块写完”冒充“整项可用”；未接线的部分明确标注。
- 未实际运行的测试写“未运行”，不要写“应该通过”。

## 发布审阅构建（模型自己完成）

先提交，再在 feature 工作树里运行一条命令：

```bash
pwsh fucode/feature-publish.ps1
```

它会自动完成：

- 确认当前不是 `main`、工作树干净；
- 停掉该 feature 上一次启动的审阅窗口；
- 读取分支和 HEAD，生成 `branch@commit` label；
- 调用 `publish-local.ps1` 构建到主工作树的 `out/review/<分支名>`；
- 读回 `review.json`，核对 label、commit 和 exe；
- **立即启动新窗口**，用户直接看效果；打印分支、commit、label、目录和 PID。

常用变体：

```bash
pwsh fucode/feature-publish.ps1 -NoRestore
pwsh fucode/feature-publish.ps1 -AllowDirty
pwsh fucode/feature-publish.ps1 -NoLaunch
```

`-AllowDirty` 只用于确认未提交改动确实要进入本次审阅构建；正常情况下应先提交。
`-NoLaunch` 只用于 CI/批量验证，不作为交付方式。

规则：

- **不需要写 `-OutputRoot` 或手动拼 label。**
- 不要直接运行裸的 `publish-local.ps1` 代替本脚本；本脚本会核对产物并启动窗口。
- 构建失败时保留完整错误，修完重新提交、重新运行；不要只发旧产物。
- 发布只会停掉**同一 feature 上一次由本脚本启动的窗口**，不会停止用户的主实例或别的 feature。
- 不会覆盖 `out/app`，不会写本地更新指针。

发布成功后，`review.json` 和 `app/build-info.json` 会包含：

```text
label    : <branch>@<short-sha>
branch   : <branch>
commit   : <short-sha>
worktree : <你的工作树绝对路径>
review   : true
```

## 交付时必须回报

在最终回复和交接卡里写清楚：

```text
任务 / feature：
分支：
HEAD commit：
审阅 label：
审阅目录：out/review/<branch-slug>
构建结果：通过 / 失败（附退出码）
窗口 PID：
已运行测试：
未运行/未验证：
未提交改动：有/无（有则说明原因）
阻塞与需要主控接线的事项：
```

不要只发“已完成”。用户需要用 label 和目录确认自己看的是哪个构建。

## 人类审阅循环

`feature-publish.ps1` 构建成功后会直接弹出带 label 的窗口，用户不需要去审阅板选择。
审阅板只是盘点和回退入口：

```bash
pwsh fucode/review-parallel.ps1 -NoBuild -Open
```

用户的反馈回到你这个会话后：

1. 在原工作树修改，不要另开工作树。
2. 重新提交、重新运行 `feature-publish.ps1`；它会先关掉你上一轮的窗口，再弹出新构建。
3. 再次回报新的 label、commit、PID 和构建结果。
4. 不要自行删除工作树；等主控确认已合入 `main` 后再清理。

## 合入与清理

用户点头后，主控运行：

```bash
pwsh fucode/feature-merge.ps1 -Name <id>
```

它要求 feature 已同步 main、两边工作树干净，只做 fast-forward；否则拒绝。合入后不要立刻删工作树，先 `feature-publish.ps1 -Main` 看集成结果。

确认 main 正常后才允许：

```bash
pwsh fucode/feature-worktree.ps1 -Name <id> -Remove
```

脚本会拒绝不干净或未合并的工作树，也会拒绝在**被删除的工作树目录内**运行。
必须在主工作树执行清理。不要用 `Remove-Item -Recurse` 或
`git worktree remove --force` 绕过检查。

## 可直接粘贴的派工 prompt

```text
你在 Fufucode 的并行 feature 工作树中执行任务，直接完成实现，不只给方案。

开工前先读：
- AGENTS.md 的“并行 Feature（2026-10-21）”段
- docs/tasks/parallel-feature-brief.md
如果这个旧工作树里没有上述文件，按本 prompt 的规则执行，不要停下来等文件。

任务 ID：
难度 / 推荐模型：
工作树：
分支：
允许修改的路径：
依赖：
验收条件：

硬规则：
1. 只在上述工作树写入；不改主工作树，不合并 main，不 push，不改写历史。
2. 一个工作树只有一个写入者；发现不属于本任务的未提交改动，停下报告。
3. 不覆盖 out/app，不写本地更新指针，不停止用户正在运行的应用。
4. 完成后先提交，再从工作树运行：
   pwsh fucode/feature-publish.ps1
   不要自行删除工作树；构建失败必须修完重新发布。
5. 最终回复必须包含：分支、HEAD commit、审阅 label、审阅目录、构建退出码、
   已运行测试、未运行项、未提交改动、阻塞与需要主控接线的事项。

先核对实际代码和已有交付；未实际运行的不标通过。任务收尾按
docs/tasks/session-handoff-protocol.md 写交接卡并主动回传。
```
