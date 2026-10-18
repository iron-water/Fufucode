# 轨道 A 任务：M6 Core 基础——设置引擎与 Core 侧 P2 批量

> MVP 已达成。本任务为 M6 的 Core 地基（UI 由 D 后续接线）。依据计划 §9（设置与认证）、§12（日志与脱敏）；UniGetUI 研究 §4.1/§6.2.5 的设置引擎经验可参考（JSON + source-gen、K 枚举集中键名、导入导出排除清单、`TEST_*` 注入口），采纳与否记 decisions.md。

## 范围（只允许写这些）

- `src/Fufucode.Core/`、`src/Fufucode.Pi/`、`tests/`、`docs/progress.md`、`docs/verification.md`

## 任务

1. **设置引擎**：应用设置存储（JSON + source-gen 序列化），键名集中管理；含模型选择/思考等级记忆、主题偏好占位、代理与自定义端点结构（§9：沿用 Pi 的模型配置结构，先支持一种）。变更事件要有——设置改了要能通知（UniGetUI 的教训）。
2. **凭据路径**：Atria key 的环境变量方式保持；为发布版预留 Windows 凭据管理器（PasswordVault）接入点（接口先立，实现 M7 前完成）。
3. **日志与脱敏**（§12）：应用级日志落地——事件类型、请求 ID、耗时、退出码、脱敏错误；默认不记 prompt/工具内容/凭据。这是目前完全缺失的一块，排障急需。
4. **Core 侧 P2 批量**（docs/records/code-review-24b5b7e.md）：P2-4（get_state 失败不回退 Activity）、P2-6（SQLite 写移出事件泵）、P2-7（恢复队列清理）、P2-15（ListSessions N+1 与日期解析文化）；逐条在 verification.md 记录修复证据。
5. D 交接项：`HistoryMapper` 镜像去重候选（progress-d.md 交轨道 A 记录）——若做，公开等价 helper 并同步 D。

## 完成标准

- 设置引擎有测试（读写、迁移、变更通知、损坏文件容错）；日志脱敏有专项断言。
- 四条 P2 有修复证据；`--selftest --realpi` 真实链路不回退。
- 进度与验证如实更新。

## 本轮派单（2026-09-30，协调者）

在 track-a-core worktree（main 分支）执行；遵守派发纪律（不 git/不构建/不装包）。

1. **`HistoryMapper` 去重候选**：将消息块/用量映射抽为 `src/Fufucode.Core/` 公开 helper（如 `MessageBlockMapper`），使 App 侧 `HistoryMapper` 的镜像实现可被替换；补 Core 测试。完成后在 progress.md 记"交轨道 D：可换用该 helper 消除镜像"。
2. **真实模型多轮对话验收（M2 收口项）**：在 `--selftest --realpi` 链路加"多轮"场景（两轮真实模型往返 + 每轮中途 abort 一次 + 终态断言），Atria 端点与 `FUFUCODE_ATRIA_API_KEY` 注入方法沿用既有 selftest 段。**只写代码与断言，不执行**；把外部验证顺序写进 docs/verification.md（构建→fakepi-build→selftest→core-tests→selftest --realpi）。
3. 完成进度如实写 docs/progress.md；阻塞写原因。

