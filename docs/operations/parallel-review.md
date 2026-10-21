# 并行 Feature 开发与审阅

日期：2026-10-21。目标：多个模型并行开发时，每个构建都有独立身份、独立输出和独立数据根，人类可以逐个启动审阅，不再抢同一个 `out/app`。

## 规则（短，不靠模型自觉）

1. `main` 只做集成，不直接承载并行 feature。
2. 一个 feature 名 = 一个分支 = 一个工作树 = 一个写入者。
3. 模型不共享工作树；同一工作树只有一个写入者。
4. 构建默认走 `fucode/publish-local.ps1 -OutputRoot out/review/<slug>`，不覆盖 `out/app`，不写本地更新指针，不停止正在运行的应用。
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
```

清理会先检查工作树是否干净、分支是否已进入 `main`，任一不满足就拒绝。

## 构建与审阅

```bash
pwsh fucode/review-parallel.ps1 -Open
pwsh fucode/review-parallel.ps1 -IncludeMain -Open
pwsh fucode/review-parallel.ps1 -LaunchSlug <slug>
pwsh fucode/review-parallel.ps1 -NoBuild
```

每个 feature 的输出：

```text
out/review/<slug>/app/Fufucode.App.exe
out/review/<slug>/review.json
out/review/<slug>/build.log
out/review/index.html
```

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

## 单个 feature 直接发布

```bash
pwsh fucode/publish-local.ps1 -OutputRoot out/review/<slug> -ReviewLabel "fix-x@abc123" -NoDeploy -NoRun
```

`-OutputRoot` 模式下不会停止现有应用、不会覆盖 `out/app`、不会写 `%LOCALAPPDATA%\Fufucode\updates\local` 指针。

## 人类反馈循环

1. 模型各自在自己的工作树实现并提交。
2. 模型用 `publish-local.ps1 -OutputRoot ... -ReviewLabel ...` 发布自己的构建。
3. 用户运行 `review-parallel.ps1 -Open` 看审阅板，按 label 选择构建。
4. 用 `-LaunchSlug` 逐个启动，窗口标题和 data-root 都指向该 feature。
5. 审阅意见回到对应会话；只有合入 main 后才用 `feature-worktree.ps1 -Remove` 清理。

## 为什么不是让模型完全自己处理

完全靠模型记忆容易出现三类事故：两个会话写同一工作树、把 `out/app` 覆盖成别人的构建、启动后被单实例守卫踢掉。上述脚本把这三件事变成默认路径；规则本身只有六条，不要求模型维护额外状态。
