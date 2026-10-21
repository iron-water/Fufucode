# 并行 Feature 执行说明（模型必读）

日期：2026-10-21。适用于所有在独立工作树里开发 Fufucode feature 的模型会话。
配套规则见 [AGENTS.md](../../AGENTS.md) 的“并行 Feature”段，命令细节见
[parallel-review.md](../operations/parallel-review.md)。

## 一句话

一个 feature = 一个分支 = 一个工作树 = 一个写入者；在自己的工作树提交，用
`publish-local.ps1 -ReviewLabel` 发布到主仓库的 `out/review/<分支>`；不要碰 `main`、
`out/app` 和别人的运行实例。

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

## 发布审阅构建

先提交，再发布。这样 label 和 commit 对得上：

```bash
git rev-parse --short=8 HEAD
pwsh fucode/publish-local.ps1 -ReviewLabel "<branch>@<short-sha>" -NoRun
```

示例：

```bash
pwsh fucode/publish-local.ps1 -ReviewLabel "feature/review-ui@a1b2c3d4" -NoRun
```

规则：

- **不需要写 `-OutputRoot`。** 脚本会自动放到主工作树的
  `out/review/<分支名>`（`/` 换成 `-`）。
- 不要省略 `-ReviewLabel`。在 feature 工作树里省略它会变成普通本地构建，
  可能覆盖 `out/app` 或写本地更新指针。
- `-NoRun` 表示只发布，不启动。审阅窗口由用户从审阅板启动。
- 发布不会停止用户正在运行的应用，也不会写
  `%LOCALAPPDATA%\Fufucode\updates\local` 指针。
- 如果脚本报告构建失败，保留完整错误，修完重新提交、重新发布；不要只发一个旧产物。

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
已运行测试：
未运行/未验证：
未提交改动：有/无（有则说明原因）
阻塞与需要主控接线的事项：
```

不要只发“已完成”。用户需要用 label 和目录确认自己看的是哪个构建。

## 人类审阅循环

用户会运行：

```bash
pwsh fucode/review-parallel.ps1 -NoBuild -Open
```

或只启动你这一项：

```bash
pwsh fucode/review-parallel.ps1 -NoBuild -LaunchSlug <branch-slug>
```

用户的反馈回到你这个会话后：

1. 在原工作树修改，不要另开工作树。
2. 重新提交、重新发布，label 使用新的 commit。
3. 再次回报新的 label、commit 和构建结果。
4. 不要自行删除工作树；等主控确认已合入 `main` 后再清理。

## 清理

只有主控或用户确认分支已合入 `main` 后，才允许：

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
   pwsh fucode/publish-local.ps1 -ReviewLabel "<branch>@<short-sha>" -NoRun
   不要省略 -ReviewLabel；不要自行删除工作树。
5. 最终回复必须包含：分支、HEAD commit、审阅 label、审阅目录、构建退出码、
   已运行测试、未运行项、未提交改动、阻塞与需要主控接线的事项。

先核对实际代码和已有交付；未实际运行的不标通过。任务收尾按
docs/tasks/session-handoff-protocol.md 写交接卡并主动回传。
```
