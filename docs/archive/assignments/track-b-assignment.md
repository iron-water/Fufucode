# 轨道 B 任务书：Fufucode 工具审批 Pi 桥接扩展

> 本文件是轨道 B agent 的启动指令。开工前先读 `docs/AI_DEVELOPMENT_PLAN.md` 第 8 节（工具审批与插件）和根目录 `AGENTS.md`（文件所有权）。

## 目标

把 M3 中与 C# 无关的扩展部分提前并行完成：一个 Pi 扩展（TypeScript），在工具执行前请求用户确认，等待桌面（现阶段由验证脚本代替）的决定，按决定放行或阻止。

## 文件范围（只允许写这些）

- `extensions/fufucode-bridge/` —— 扩展本体
- `scripts/bridge-verify/` —— 验证驱动脚本（TS/Node，充当"假桌面"自动应答）
- `docs/archive/progress/progress-b.md` —— 进度与验证记录（自建）

## 依据（本地已装版本，勿凭记忆写代码）

- Pi 版本：本机全局 `@earendil-works/pi-coding-agent@0.87.1`；也可用 `runtime/pi`（A 准备的应用管理运行时，只读，不得写入）。
- 官方文档在包内：`docs/extensions.md`（tool_call 钩子、阻止返回值、ctx.ui API）、`docs/rpc-extension-ui.md`（extension_ui_request/response 子协议）、`docs/security.md`。
- 所选版本的阻止返回值、处理器顺序、参数可变性、错误行为必须从文档/源码核实，不凭旧示例。

## 实现要求（对应计划 §8.2）

1. 在 `tool_call` 执行前路径请求确认，使用 `ctx.ui.confirm` / `select` 等官方接口经 `extension_ui_request` 往返。
2. 决定语义：允许 → 放行；拒绝 → 阻止执行；取消/弹窗超时/连接丢失 → 一律按拒绝。超时由 agent 侧 `timeout` 字段自动解决时，按其返回值处理。
3. 默认策略：文件读取/搜索类按配置可自动放行；写入与 shell 一律确认；未知自定义工具默认确认。策略可配置，不硬编码。
4. 一次批准只对应具体请求与参数；参数被其他处理器修改后不得套用旧批准。
5. 审批等待不得阻塞 stdout 消费——只阻塞该工具的继续执行。
6. 扩展加载失败必须可诊断：桌面侧（现阶段为驱动脚本）能感知"审批未生效"，不误报已保护。

## 验收（对应计划 §11 的 A01–A03，全部要有证据）

- **A01**：允许才执行；拒绝和超时后，目标文件确实未改变、命令确实未执行（检查文件系统状态，不能只检查收到了拒绝消息）。
- **A02**：审批等待期间，stdout 持续消费、其他事件（如另一会话事件）不被阻塞。
- **A03**：桥接扩展缺失或加载失败时，驱动脚本报告"审批不可用"而非静默继续。
- 用 `scripts/bridge-verify/` 驱动真实 Pi（`--mode rpc`）在临时工作区完成上述验证；把命令、结果、证据路径写入 `docs/archive/progress/progress-b.md`。

## 协作规则

- 遵守 `AGENTS.md`：不碰 `src/`、`tests/`、`scripts/` 其他子目录、`runtime/`；进度只写 `docs/archive/progress/progress-b.md`。
- Git：只提交自己独占的文件到 `main`（或先在分支 `track-b` 工作后合并）。
- 遇到所选版本能力与计划不符：记入 progress-b.md 并继续无依赖部分，不静默变通。
