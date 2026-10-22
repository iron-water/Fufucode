# 并行 Feature 开发与审阅

日期：2026-10-21。目标：多个模型并行开发时，每个构建都有独立身份、独立输出和独立数据根，人类可以逐个启动审阅，不再抢同一个 `out/app`。

## 规则（短，不靠模型自觉）

1. `main` 只做集成，不直接承载并行 feature。
2. 一个 feature 名 = 一个分支 = 一个工作树 = 一个写入者。
3. 模型不共享工作树；同一工作树只有一个写入者。
4. 模型收尾必须运行 `fucode/feature-publish.ps1` 自主发布，自动写到主工作树 `out/review/<分支名>` 并立即启动窗口；同 feature 重新发布只停掉自己上一轮的窗口，不覆盖 `out/app`，不写本地更新指针，不停用户的主实例。
5. 清理只允许 `fucode/feature-worktree.ps1 -Remove`：工作树必须干净且分支已合并，否则拒绝。
6. 审阅构建的窗口标题必须带 `branch@commit` label；没有 label 的窗口不作为审阅对象。

## 新建/清理工作树

```bash
pwsh fucode/feature-worktree.ps1 -Name review-ui
pwsh fucode/feature-worktree.ps1 -Name review-ui -Remove
```

新建会得到：

```text
branch  : feature/review-ui
worktree: C:\Projects\Fucode\Fufucode\.claude\worktrees\review-ui
review  : C:\Projects\Fucode\Fufucode\out\review\feature-review-ui
```

清理会先检查工作树是否干净、分支是否已进入 `main`，任一不满足就拒绝。
必须在主工作树执行清理；在要删除的工作树目录内运行会被拒绝，避免删到一半留下残目录。

## 构建与审阅

```bash
pwsh fucode/review-parallel.ps1 -NoBuild -Open
pwsh fucode/review-parallel.ps1 -NoBuild -LaunchSlug <slug>
pwsh fucode/review-parallel.ps1 -Open
pwsh fucode/review-parallel.ps1 -IncludeMain -Open
```

`-NoBuild` 只盘点模型已经发布好的构建，适合“模型各自发布、用户逐个看”的常态；
不带 `-NoBuild` 会由主工作树串行重建全部 feature。

每个 feature 的输出：

```text
out/review/<slug>/app/Fufucode.App.exe
out/review/<slug>/review.json
out/review/<slug>/build.log
out/review/index.html
```

`<slug>` 由分支名固定生成（`/` 换成 `-`），不随 commit 变化：
`feature/review-ui` → `out/review/feature-review-ui`，`main` → `out/review/main`。
这样重新提交后审阅板仍能找到同一个目录，并能判断构建是否落后于当前 HEAD。

`review.json` 和 `app/build-info.json` 都带：

- `label`：默认 `branch@commit`
- `branch`
- `commit`
- `worktree`
- `review: true`

启动命令由审阅板给出，使用：

```text
--project <repo> --data-root out/review/<slug>/data --parallel-review --review-label <label>
```

- `--data-root` 隔离设置、会话索引和日志；
- `--parallel-review` 跳过单实例守卫，多个审阅窗口可以同时存在；
- `--review-label` 显示在窗口标题里，避免看错构建。

## 单个 feature 自主发布

模型在 feature 工作树里执行一条命令：

```bash
pwsh fucode/feature-publish.ps1
```

它做五件事：确认不是 `main`、检查工作树干净、停掉同 feature 上一轮窗口、用
`branch@commit` 发布到主工作树的 `out/review/<分支名>`、读回 `review.json` 核对
label/commit/exe 后立即启动新窗口。模型不需要知道主仓库路径，也不应该让用户代为构建。

- 有未提交改动时默认拒绝；确认要一起构建时加 `-AllowDirty`。
- 构建失败会返回非零退出码，模型必须修完重新发布。
- 只停掉同一 feature 上一次由本脚本启动的窗口；不停止用户主实例或别的 feature。
- 不覆盖 `out/app`，不写 `%LOCALAPPDATA%\Fufucode\updates\local` 指针。
- 裸 `publish-local.ps1 -ReviewLabel ...` 只作为脚本不可用时的故障回退。
- 模型必须按 [parallel-feature-brief.md](../tasks/parallel-feature-brief.md) 回报 label、commit、目录、PID 和构建结果。

## 人类反馈循环

1. 模型各自在自己的工作树实现并提交。
2. 模型运行 `feature-publish.ps1`，构建成功后窗口直接出现；用户不需要去审阅板选。
3. 同一 feature 再次发布时，旧窗口先关闭，新窗口自动出现。
4. 审阅板 `review-parallel.ps1 -NoBuild -Open` 只用于盘点/回退。
5. 审阅意见回到对应会话；只有合入 main 后才用 `feature-worktree.ps1 -Remove` 清理。

模型侧的执行规则、发布命令和交付格式见 [parallel-feature-brief.md](../tasks/parallel-feature-brief.md)。

## 为什么不是让模型完全自己处理

完全靠模型记忆容易出现三类事故：两个会话写同一工作树、把 `out/app` 覆盖成别人的构建、启动后被单实例守卫踢掉。上述脚本把这三件事变成默认路径；规则本身只有六条，不要求模型维护额外状态。
