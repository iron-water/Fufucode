# Worktree 清单与回收边界

日期：2026-10-21。只登记，不自动删除。

主仓库：`C:\Projects\Fucode\Fufucode`，分支 `main`。

当前 `git worktree list`：

| 路径 | 分支 | 处理 |
|---|---|---|
| `C:\Projects\Fucode\Fufucode` | `main` | 唯一主仓库 |
| `C:\Projects\Fucode\Fufucode\.claude\worktrees\fufu-01-desktop` | `feature/fufu-01-desktop` | 活跃 feature 工作树，唯一写入者 |
| `C:\Projects\Fucode\Fufucode\.claude\worktrees\ui-color-system` | `feature/ui-color-system` | 活跃 feature 工作树，唯一写入者 |
| `C:\Users\mio\.herdr\worktrees\Fufucode\worktree-brave-river-ba40` | `worktree/brave-river-ba40` | 外部 herdr 工作树；归属确认前不动 |

历史工作树（liquidglass-v3、mem-optimization、terminal-p0、perf-monitor、autodev-plugin-extract 等）已随并行会话收编/移除，不再登记为活跃工作树。若以后重新创建，必须：

1. 先确认任务、分支和唯一写入者。
2. 有 unique commit 的工作树先审核/收编，不能直接递归删除。
3. 移除使用 `git worktree remove <path>`，不用文件管理器递归删除。
4. 外部 `.herdr` 工作树先确认所属会话和未提交内容。
