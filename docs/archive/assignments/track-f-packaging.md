# 轨道 F 任务书：M7 打包 spike——发布布局、干净启动验证与测量

> 依据计划 §10 M7、§3（运行时固定规则）、§12（性能口径）；UniGetUI 研究 §5.4（打包/CI）可作参考，非需求。目的：把发布环节的未知（运行时怎么带、干净机器能不能跑、体积多大、启动多快）在写代码的阶段就拆掉，M7 只剩收尾。开工前：按 AGENTS.md 用 `git worktree add` 建自己的工作区（基于最新 main）。

## 范围（只允许写这些）

- `scripts/packaging/` —— 打包脚本与启动器
- `labs/packaging/` —— spike 产物、测量报告（大文件不入库，报告入库）
- `docs/archive/progress/progress-f.md`、`docs/decisions.md`（追加 `[轨道F]` 条目）

**不碰**：`src/`、`tests/`、`runtime/`、其他 `scripts/` 子目录。打包对象是 main 上已能构建的解决方案（当前为 TestApp 欢迎页 + App 外壳 `--selftest`），不要求包含未完成功能。

## 任务

1. **可重复打包脚本**：`dotnet publish` self-contained win-x64 + 携带应用管理的 `runtime/node` 与 `runtime/pi`（内容按 A 的 manifest/lockfile 固定）+ 启动器把 `PI_CODING_AGENT_DIR`、`--session-dir` 指向 `%LocalAppData%\Fufucode`。产物为一个 zip 或目录布局。
2. **干净启动验证**：把产物解压到非开发路径（如 `%TEMP%\fff-pkg-test\`），在未设置全局 npm/pi PATH 的环境启动：`--selftest` 全绿；Pi 子进程从包内运行时启动成功；无模型 RPC 查询可用。不得依赖开发机上的全局安装。
3. **测量（计划 §12 口径）**：包体积（分列：.NET 运行时 / node / pi / 应用代码）；冷启动到窗口可交互；Pi 就绪时间（进程启动到 `get_state` 成功）；关闭后进程残留检查。数据落盘 `labs/packaging/`。
4. **升级/回退设计文档**：`runtime/<version>` 并存布局、升级时保留用户会话与设置、会话格式兼容性检查点、回退边界（依计划 §3：保留旧程序不等于可安全降级全部数据）。写入 `docs/decisions.md` `[轨道F]`。
5. **许可合规清单**：Pi 包与其依赖的 license、.NET/Avalonia 等主要 NuGet 包 license、`reference/UniGetUI` 仅作内部参考不分发的说明。落 `labs/packaging/licenses.md`。

## 完成标准

- 干净目录解压 → 启动 → selftest 绿 → 无模型 RPC 查询成功，全程不依赖开发环境；命令与证据落盘。
- 测量数据为实测值并标注口径；缺什么（如签名、安装器）如实列为未验证，不虚构。
- 升级/回退决策入 decisions.md；进度如实更新。
