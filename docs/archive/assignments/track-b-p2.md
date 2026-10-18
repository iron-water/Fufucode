# 轨道 B 任务：extensions/scripts 侧审查 P2 小批

> 小任务，零冲突（都在你独占领地）。依据 docs/records/code-review-24b5b7e.md 的 P2-11、P2-16。开工前 `git merge main`。

## 任务

1. **P2-11**：`scripts/bridge-verify/run.mjs` 与 `scripts/demo-verify/run.mjs` 硬编码 `PROJECT_ROOT = "C:\\Projects\\Fufucode"`——改为从脚本自身位置推导仓库根（`import.meta.url` 上溯），或支持 `FFF_PROJECT_ROOT` 环境变量覆盖；任何 worktree 里运行都不得再混用共享工作区的文件。改完在两个 worktree 各跑一遍对应验证套件证明等价。
2. **P2-16**：`extensions/fufucode-bridge/policy.ts` 配置解析失败静默回退默认策略——回退时经 `ctx.ui.notify` 发一条诊断（"自定义策略解析失败，已回退默认 confirm 策略"），方向保持安全（默认 confirm 不变）。
3. 两项均按现有验证模式补断言（路径推导用临时目录验证；notify 用 demo-verify 同款侦听）。

## 完成标准

- 从任意 worktree 运行两套验证脚本均正确定位仓库根；策略回退有诊断且行为不变。
- 证据与记录写入 progress-b.md。
