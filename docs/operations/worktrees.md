# Worktree 清单与回收边界

日期：2026-10-21。只登记，不自动删除。

主仓库：`C:\Projects\Fucode\Fufucode`，分支 `main`。

| 路径 | 分支 | 相对 main 的 unique commit | 处理 |
|---|---|---:|---|
| `C:\Projects\Fucode\Fufucode\.claude\worktrees\autodev-plugin-extract` | `agent/autodev-plugin-extract` | 0 | 确认无在途工作后可 `git worktree remove` |
| `C:\Projects\Fucode\Fufucode\.claude\worktrees\mem-optimization` | `worktree-mem-optimization` | 3 | 有未合并提交，先审核/收编 |
| `C:\Projects\Fucode\Fufucode\.claude\worktrees\perf-monitor` | `feature/perf-monitor` | 0 | 确认无在途工作后可回收 |
| `C:\Projects\Fucode\Fufucode\.claude\worktrees\terminal-p0` | `worktree-terminal-p0` | 3 | 有未合并提交，先审核/收编 |
| `C:\Projects\Fucode\Fufucode-lgv3` | `liquidglass-v3` | 8 | 有未合并提交；完整 checkout，暂不移动/删除 |
| `C:\Users\mio\.herdr\worktrees\Fufucode\worktree-brave-river-ba40` | `worktree/brave-river-ba40` | 未核对 | 外部工具工作树，归属确认前不动 |
| `C:\Users\mio\.omp\wt\wt-20261010-*`（5 个） | `wt/20261010-*` | 未核对 | 旧 OMP 工作树；逐个核对后回收 |

规则：

1. 有 unique commit 的工作树，先确认是否已收编；不能直接递归删除。
2. 移除使用 `git worktree remove <path>`，不用文件管理器递归删除。
3. 外部 `.herdr`/`.omp` 工作树先确认所属会话和未提交内容。
4. `Fufucode-lgv3` 是 `liquidglass-v3` 的登记工作树，不是重复仓库；在皮肤工作未收编前保留。
