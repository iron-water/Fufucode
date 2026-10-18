# 轨道 B 任务书：验证执行员（无人值守冲刺的常驻质量岗）

> 你的智力不需要发明方案，你的额度适合做**高频、机械、规格完全明确**的事：执行验证并留下证据。这是整个无人值守流程的"仪表盘"——协调者与各轨道据此判断状态。开工前 `git merge main`。

## 文件范围（只允许写这些）

- `docs/records/verify-log.md`（新建，本轨独占——验证执行日志）
- `docs/archive/progress/progress-b.md`
- `extensions/`（fixture 扩充仍归你）

## 常驻职责（每轮其他轨道交付合并前/后执行）

1. **固定验证组**（在 main 工作区或带 `FFF_PI_RUNTIME` 的任意 worktree）：
   - `dotnet build Fufucode.sln`（记录错误/警告数）
   - `dotnet run --project src/Fufucode.App -- --selftest` 与 `-- --selftest --realpi`
   - `dotnet run --project tests/Fufucode.Core.Tests/...`
   - `pwsh -File scripts/packaging/verify-clean-start.ps1`（打包相关变更时）
   - `scripts/bridge-verify` 与 `demo-verify`（扩展相关变更时）
2. **记录纪律**：每次执行在 `docs/records/verify-log.md` 追加一行——时间戳、main 提交 hash、各命令结果（PASS/FAIL/SKIP 数）、异常摘要。**只记录事实，不做修复、不做判断性结论**（结论归协调者）。
3. **异常即上报**：任何 FAIL——原文复制错误输出到 verify-log 并在 progress-b.md 置顶提示，不尝试修复。

## 扩充任务（额度用不完时）

- 为两个 fixture 扩展增加更多管理页验收场景（与轨道 D 的 progress 需求联动）。
- 使用文档草稿：`docs/usage-draft.md`（基于 mock 模式真实运行的截图与步骤；截图放 `docs/img/`，本轨可写）。

## 红线

- 不修任何 FAIL（那是原轨道的事）；不碰 `src/`、`tests/`、`scripts/`、`runtime/`。
- 验证执行失败（环境缺 runtime 等）按库内既有方法处理（`FFF_PI_RUNTIME` / junction），并如实记录。
