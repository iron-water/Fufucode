# R01 验证与交付记录

日期：2026-10-05（America/Tijuana）。仅验证研究交付，不是产品验收。

| 实际执行 | 结果 | 证据 |
|---|---|---|
| `runtime/node/node.exe --version` | exit 0，v24.15.0 | report.md 版本表 |
| `runtime/node/node.exe docs/research-results/R01/collect-evidence.mjs` | exit 0；本地包/文件哈希、GitHub 元数据与 dsh tree，tree 未截断 | evidence/local-manifest.json、upstream-metadata.json、dsh-tree.json |
| `runtime/node/node.exe docs/research-results/R01/fetch-upstream.mjs` | exit 0；25 个 dsh 固定 commit 源文件/文档 | evidence/dsh-sources.json 与 dsh-* 快照 |
| `runtime/node/node.exe docs/research-results/R01/fetch-pi-reference.mjs` | exit 0；5 个 Pi 固定 commit 文件；两份文档与实际安装包逐字节相同 | evidence/pi-reference-sources.json 与 pi-upstream-* |
| `runtime/node/node.exe docs/research-results/R01/validate-delivery.mjs` | exit 0，89 检查、0 失败 | verification.json |
| Git 只读基线/状态核对 | main；HEAD 未变，仍为 4d7c2a26cbdab744924dadb466990783ef2f7725 | evidence/final-git-status.txt、verification.json；末次用 --no-optional-locks |

89 项包含本地引用文件哈希、安装包源码快照哈希、上游固定 commit 快照哈希、两份 Pi 文档字节相同、Markdown fence、关键章节与本地链接、HEAD/分支。它们不是 89 个功能测试。

保留开工前已有的 demo-verify 修改与未提交设计/HTML/任务书。所有新写入位于 R01；产品源码仅读取，没有安装插件、更新 Pi、访问凭据、自建 worktree、Git 写操作或执行实验。

末次只读 status 另出现 `docs/research-results/R02/claim.md`；属于其他研究目录的并行工作，本任务没有读取或修改它，不能归入 R01 交付。

研究期遇到过命令输出 UTF-8 显示/RTK 截断、一个 rg 参数错误与一个临时只读 Node regex 转义错误；后续使用 UTF-8 与完整源文件快照定向读取，取证脚本均退出 0。未因这些读取问题执行修复、放宽产品测试或改变任务权限。

**未验证**：Pi runtime reload/运行中工具换代、迟到 UI 回复与 editor 清理的真实行为、自有 rich form、Avalonia 页面卸载/布局/焦点、WebView/ConPTY 资源释放、ALC 卸载、worker 崩溃隔离、dsh HMR 运行。report.md 的 PoC 与行为验收是待授权方案。runtime-baseline.md 中既有探针只作历史实测来源，本轮未重跑。

交付：claim.md 已改为“待主 agent 审查”。本报告不自动进入 docs/decisions.md，不提交或集成。主 agent 负责决定后续实施范围和实验授权。
