$ErrorActionPreference = 'Stop'
$root = 'C:\Users\21894\.codex\worktrees\track-f-packaging\Fufucode'
Add-Content -Path (Join-Path $root 'docs/decisions.md') -Value @'

## [轨道F] 升级/回退设计：`runtime/<version>` 并存布局

- 2026-09-28：发布布局采用 `runtime/<version>/` 并存（node + pi 各自按版本目录），升级时保留旧版本目录而非就地覆盖；旧版本只在确认新版本通过有限验证后（真实 `get_state` + 一次无副作用查询 + 自测全绿）才允许清理。理由：计划 §3 要求"保留旧运行环境"，就地覆盖一旦失败无法回退。

- 2026-09-28：升级保留用户会话与设置——`%LocalAppData%\Fufucode` 下的 `pi-agent`、`sessions/`、`desktop.db`、`logs/`、`exports/` 全部属于用户数据区，与 `runtime/` 代码区分离，升级操作不触碰该目录。理由：会话与配置是用户状态，不应因运行时版本变更丢失。

- 2026-09-28：会话格式兼容性检查点——新版本首次启动时执行一次 `get_state` 与一次 `get_entries`（带已有 `since` 游标），若返回形状与现有 DTO 不兼容则拒绝升级并提示用户；`get_entries` 的 `since` 游标已实测可用（runtime-baseline.md），可作为增量语义的探针。理由：仅检查协议形状，不做全量历史重放，成本低且能捕获破坏性变更。

- 2026-09-28：回退边界——保留旧程序不等于可以安全降级全部数据。已落盘的会话文件（Pi 的 JSONL 会话）与 SQLite 元数据（`desktop.db`）由新版本写入后，旧版本可能无法识别其 schema 或协议字段，因此回退只保证"可启动旧版本并读取旧版本创建的历史"，不保证新版本产生的数据在旧版本下可读；该限制写入 `docs/archive/progress/progress-f.md` 未验证项。理由：与计划 §3"保留旧程序不等于可安全降级全部数据"一致，避免给用户虚假的回退承诺。

- 2026-09-28：自动 Pi 升级被禁止（计划 §3：不在应用启动时自动拉取 latest，也不自动执行 `pi update`）。Pi 版本变更由用户显式触发，触发后先在新目录安装、验证，再切换当前版本指针。理由：Pi 协议变更会破坏已冻结的 `IChatSession` 适配层，隐式升级等于隐式破坏桌面端。
'@